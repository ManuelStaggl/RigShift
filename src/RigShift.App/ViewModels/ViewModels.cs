using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>A profile as shown in the tray popup, the tray menu and the profile page.</summary>
public sealed partial class ProfileItem(Profile profile) : ObservableObject
{
    public Profile Profile { get; } = profile;

    public string Name => Profile.Name;

    /// <summary>Left to right, as the displays stand on the desk.</summary>
    public IReadOnlyList<string> DisplayLines { get; } = profile.Displays
        .OrderBy(d => d.PositionX)
        .ThenBy(d => d.PositionY)
        .Select(Describe)
        .ToList();

    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial bool IsDefault { get; set; }

    [ObservableProperty]
    public partial string? CheckMessage { get; set; }

    private static string Describe(DisplayAssignment display)
    {
        double hertz = display.RefreshDenominator == 0 ? 0 : (double)display.RefreshNumerator / display.RefreshDenominator;
        string text = string.Create(Loc.Instance.Culture,
            $"{SwitchMessages.NameOf(display.Identity)} · {display.Width} × {display.Height} @ {hertz:0.##} Hz");
        if (display.IsPrimary)
        {
            text += " · " + Loc.Instance["Profile_Primary"];
        }

        if (display.IsOptional)
        {
            text += " · " + Loc.Instance["Profile_Optional"];
        }

        return text;
    }
}

public sealed partial class TrayPopupViewModel : ObservableObject
{
    private readonly IAppShell _shell;

    public TrayPopupViewModel(ProfileCatalog catalog, SwitchCoordinator coordinator, IAppShell shell)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        Catalog = catalog;
        Coordinator = coordinator;
        _shell = shell;
        catalog.Changed += (_, _) => OnPropertyChanged(nameof(ActiveText));
        Loc.Instance.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ActiveText));
    }

    public event EventHandler? CloseRequested;

    public ProfileCatalog Catalog { get; }

    public SwitchCoordinator Coordinator { get; }

    public string ActiveText => Catalog.ActiveProfile is { } active
        ? Loc.Format("Tray_Active", active.Name)
        : Loc.Instance["Tray_ActiveNone"];

    [RelayCommand]
    private async Task SwitchAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        await Coordinator.SwitchAsync(item.Profile);
    }

    [RelayCommand]
    private void Open()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow();
    }

    [RelayCommand]
    private void Exit() => _shell.Quit();
}

public sealed partial class ProfilesViewModel(ProfileCatalog catalog, SwitchCoordinator coordinator, ILogger log) : ObservableObject
{
    public ProfileCatalog Catalog => catalog;

    public SwitchCoordinator Coordinator => coordinator;

    public string EmptyMessage => Loc.Format("Profiles_EmptyText", catalog.ProfileDirectory);

    [RelayCommand]
    private async Task ApplyAsync(ProfileItem? item)
    {
        if (item is not null)
        {
            await coordinator.SwitchAsync(item.Profile);
        }
    }

    [RelayCommand]
    private async Task CheckAsync(ProfileItem? item)
    {
        if (item is not null && await coordinator.CheckAsync(item.Profile) is { } result)
        {
            item.CheckMessage = SwitchMessages.DescribePlan(result.Plan);
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => catalog.ReloadAsync(CancellationToken.None);

    [RelayCommand]
    private void OpenFolder() => ShellFolders.Open(catalog.ProfileDirectory, log);
}

public sealed record Choice(string? Key, string Name);

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AppPaths _paths;
    private readonly ILogger _log;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, ProfileCatalog catalog, AppPaths paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _catalog = catalog;
        _paths = paths;
        _log = log.ForContext<SettingsViewModel>();
    }

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    public ObservableCollection<Choice> LanguageChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedDefaultProfile { get; set; }

    [ObservableProperty]
    public partial bool ApplyDefaultOnStartup { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    [ObservableProperty]
    public partial double? ConfirmTimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedLanguage { get; set; }

    public void Load()
    {
        _loading = true;
        try
        {
            AppSettings current = _settings.Current;

            ProfileChoices.Clear();
            ProfileChoices.Add(new Choice(null, Loc.Instance["Settings_None"]));
            foreach (ProfileItem item in _catalog.Items)
            {
                ProfileChoices.Add(new Choice(item.Profile.Id.ToString("D"), item.Name));
            }

            SelectedDefaultProfile = ProfileChoices.FirstOrDefault(c => c.Key == current.DefaultProfileId?.ToString("D")) ?? ProfileChoices[0];

            LanguageChoices.Clear();
            LanguageChoices.Add(new Choice(null, Loc.Instance["Settings_LanguageSystem"]));
            LanguageChoices.Add(new Choice("en", "English"));
            LanguageChoices.Add(new Choice("de", "Deutsch"));
            SelectedLanguage = LanguageChoices.FirstOrDefault(c => c.Key == current.Language) ?? LanguageChoices[0];

            ApplyDefaultOnStartup = current.ApplyDefaultProfileOnStartup;
            ConfirmTimeoutSeconds = current.ConfirmTimeoutSeconds;
            StartWithWindows = _settings.Autostart.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    [RelayCommand]
    private void OpenProfileFolder() => ShellFolders.Open(_paths.Profiles, _log);

    [RelayCommand]
    private void OpenLogFolder() => ShellFolders.Open(_paths.Logs, _log);

    partial void OnSelectedDefaultProfileChanged(Choice? value)
    {
        if (!_loading && value is not null)
        {
            Guid? id = value.Key is { } key ? Guid.Parse(key) : null;
            Persist(s => s with { DefaultProfileId = id });
        }
    }

    partial void OnApplyDefaultOnStartupChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { ApplyDefaultProfileOnStartup = value });
        }
    }

    partial void OnConfirmTimeoutSecondsChanged(double? value)
    {
        if (!_loading && value is { } seconds)
        {
            Persist(s => s with { ConfirmTimeoutSeconds = (int)Math.Clamp(Math.Round(seconds), 0, 120) });
        }
    }

    partial void OnSelectedLanguageChanged(Choice? value)
    {
        if (!_loading && value is not null)
        {
            Persist(s => s with { Language = value.Key });
        }
    }

    partial void OnStartWithWindowsChanged(bool value)
    {
        if (_loading)
        {
            return;
        }

        try
        {
            _settings.Autostart.SetEnabled(value);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log.Warning(ex, "Autostart could not be changed");
        }
    }

    private void Persist(Func<AppSettings, AppSettings> change) => _ = PersistAsync(change);

    private async Task PersistAsync(Func<AppSettings, AppSettings> change)
    {
        try
        {
            await _settings.UpdateAsync(change, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Settings could not be saved");
        }
    }
}

public sealed record DisplayRow(string Name, string State, string Mode, string Edid, string TargetPath, string AdapterPath);

public sealed record AudioRow(string Name, string Direction, string State);

public sealed partial class DiagnosticsViewModel(
    IDisplayConfigurator display, IAudioController audio, SwitchCoordinator coordinator, AppPaths paths, ILogger log) : ObservableObject
{
    public ObservableCollection<DisplayRow> Displays { get; } = [];

    public ObservableCollection<AudioRow> AudioDevices { get; } = [];

    public ObservableCollection<SwitchRecord> History => coordinator.History;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        ErrorMessage = null;
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => display.QueryAsync(CancellationToken.None));
            Displays.Clear();
            foreach (AttachedDisplay attached in snapshot.Displays)
            {
                Displays.Add(ToRow(attached));
            }

            IReadOnlyList<AudioDeviceInfo> render = await Task.Run(() => audio.ListAsync(AudioDirection.Render, CancellationToken.None));
            IReadOnlyList<AudioDeviceInfo> capture = await Task.Run(() => audio.ListAsync(AudioDirection.Capture, CancellationToken.None));
            AudioDevices.Clear();
            foreach (AudioDeviceInfo device in render.Concat(capture))
            {
                AudioDevices.Add(ToRow(device));
            }
        }
        catch (Exception ex) when (ex is Win32Exception or COMException or InvalidOperationException)
        {
            log.Error(ex, "Diagnostics refresh failed");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => ShellFolders.Open(paths.Logs, log);

    private static DisplayRow ToRow(AttachedDisplay display)
    {
        string state = display.IsActive ? Loc.Instance["Diag_Active"]
            : display.IsAvailable ? Loc.Instance["Diag_Connected"]
            : Loc.Instance["Diag_NotReady"];

        string mode = display.ActiveMode is { } m
            ? string.Create(Loc.Instance.Culture,
                $"{m.Width} × {m.Height} @ {(m.RefreshDenominator == 0 ? 0 : (double)m.RefreshNumerator / m.RefreshDenominator):0.##} Hz ({m.PositionX}, {m.PositionY})")
            : "–";

        string edid = display.Identity.EdidManufacturerId == 0
            ? "–"
            : string.Create(CultureInfo.InvariantCulture, $"{display.Identity.EdidManufacturerId:X4}:{display.Identity.EdidProductCodeId:X4}");

        return new DisplayRow(SwitchMessages.NameOf(display.Identity), state, mode, edid, display.Identity.TargetDevicePath, display.Identity.AdapterDevicePath);
    }

    private static AudioRow ToRow(AudioDeviceInfo device)
    {
        string state = (device.IsActive ? Loc.Instance["Diag_Active"] : Loc.Instance["Diag_Inactive"])
            + (device.IsDefault ? " · " + Loc.Instance["Diag_Default"] : string.Empty);
        string direction = device.Direction == AudioDirection.Render ? Loc.Instance["Diag_Playback"] : Loc.Instance["Diag_Recording"];
        return new AudioRow(device.Endpoint.FriendlyName, direction, state);
    }
}
