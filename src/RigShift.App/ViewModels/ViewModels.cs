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

    /// <summary>Known symbol key, or <c>null</c> for no symbol.</summary>
    public string? IconKey => ProfileIcons.Normalize(Profile.Icon);

    /// <summary>Name for screen readers; the active state is also shown as text and check mark, not only by color.</summary>
    public string AccessibleName => IsActive ? $"{Name}, {Loc.Instance["Profile_Active"]}" : Name;

    /// <summary>Left to right, as the displays stand on the desk.</summary>
    public IReadOnlyList<string> DisplayLines { get; } = profile.Displays
        .OrderBy(d => d.PositionX)
        .ThenBy(d => d.PositionY)
        .Select(Describe)
        .ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
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
        catalog.Changed += (_, _) => OnStatusChanged();
        Loc.Instance.PropertyChanged += (_, _) => OnStatusChanged();
    }

    public event EventHandler? CloseRequested;

    public ProfileCatalog Catalog { get; }

    public SwitchCoordinator Coordinator { get; }

    /// <summary>Shown below the header only when no profile row is marked active.</summary>
    public string? StatusText => Catalog.IsEmpty ? Loc.Instance["Tray_NoProfiles"]
        : !Catalog.Items.Any(i => i.IsActive) ? Loc.Instance["Tray_ActiveNone"]
        : null;

    public bool HasStatusText => StatusText is not null;

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
    private void Settings()
    {
        CloseRequested?.Invoke(this, EventArgs.Empty);
        _shell.ShowMainWindow(typeof(Views.Pages.SettingsPage));
    }

    [RelayCommand]
    private void Exit() => _shell.Quit();

    private void OnStatusChanged()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatusText));
    }
}

public sealed partial class ProfilesViewModel(ProfileCatalog catalog, SwitchCoordinator coordinator, ProfileDialogs dialogs, ILogger log) : ObservableObject
{
    public ProfileCatalog Catalog => catalog;

    public SwitchCoordinator Coordinator => coordinator;

    public string EmptyMessage => Loc.Format("Profiles_EmptyText", catalog.ProfileDirectory);

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial Wpf.Ui.Controls.InfoBarSeverity StatusSeverity { get; set; }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        if (await dialogs.CreateFromCurrentAsync() is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private async Task EditAsync(ProfileItem? item)
    {
        if (item is not null && await dialogs.EditAsync(item.Profile) is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        Profile copy = item.Profile with
        {
            Id = Guid.NewGuid(),
            Name = ProfileEditing.UniqueName(Loc.Format("Profile_CopyName", item.Name), catalog.Profiles.Select(p => p.Name)),
        };
        await RunStoreActionAsync(() => catalog.SaveAsync(copy, CancellationToken.None), Loc.Format("Status_Duplicated", copy.Name));
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileItem? item)
    {
        if (item is not null && await ProfileDialogs.ConfirmDeleteAsync(item.Name))
        {
            await RunStoreActionAsync(() => catalog.DeleteAsync(item.Profile, CancellationToken.None), Loc.Format("Status_Deleted", item.Name));
        }
    }

    [RelayCommand]
    private void CreateShortcut(ProfileItem? item)
    {
        if (item is null || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        string title = "RigShift – " + RigShift.Windows.Shell.ShortcutWriter.SafeFileName(item.Name);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), title + ".lnk");
        try
        {
            RigShift.Windows.Shell.ShortcutWriter.Create(
                file, executable, $"apply \"{item.Name.Replace("\"", "\\\"", StringComparison.Ordinal)}\"", Loc.Format("Shortcut_Description", item.Name));
            log.Information("Shortcut {File} created for profile {Profile}", file, item.Name);
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            log.Error(ex, "Shortcut {File} could not be created", file);
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private async Task RunStoreActionAsync(Func<Task> action, string success)
    {
        try
        {
            await action();
            ShowStatus(success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Error(ex, "Profile store action failed");
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string message, Wpf.Ui.Controls.InfoBarSeverity severity = Wpf.Ui.Controls.InfoBarSeverity.Success)
    {
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

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

public sealed record Choice(string? Key, string Name)
{
    /// <summary>Screen readers and type-ahead in combo boxes read the display name.</summary>
    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AppPaths _paths;
    private readonly UpdateService _updates;
    private readonly ILogger _log;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, ProfileCatalog catalog, AppPaths paths, UpdateService updates, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _catalog = catalog;
        _paths = paths;
        _updates = updates;
        _log = log.ForContext<SettingsViewModel>();
        _updates.StateChanged += (_, _) => RefreshUpdateStatus();
        RefreshUpdateStatus();
    }

    public string VersionText => Loc.Format(_updates.IsInstalled ? "Settings_Version" : "Settings_VersionDev", _updates.CurrentVersion);

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = string.Empty;

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

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => _updates.CheckNowAsync();

    private bool CanCheckForUpdates() => _updates.CanCheck;

    private void RefreshUpdateStatus()
    {
        string? lastChecked = _updates.LastChecked?.ToString("g", Loc.Instance.Culture);
        UpdateStatusText = _updates.State switch
        {
            UpdateState.NotInstalled => Loc.Instance["Update_StatusNotInstalled"],
            UpdateState.NotChecked => Loc.Instance["Update_StatusNotChecked"],
            UpdateState.Checking => Loc.Instance["Update_StatusChecking"],
            UpdateState.Downloading => Loc.Format("Update_StatusDownloading", _updates.TargetVersion ?? "?"),
            UpdateState.UpToDate => Loc.Format("Update_StatusUpToDate", lastChecked ?? "?"),
            UpdateState.Ready => Loc.Format("Update_Ready", _updates.TargetVersion ?? "?"),
            _ => Loc.Instance["Update_StatusFailed"],
        };
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
    }

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
