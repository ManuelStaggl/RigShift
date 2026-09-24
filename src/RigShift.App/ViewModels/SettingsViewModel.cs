using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using Serilog;

namespace RigShift.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject, IHotkeyField
{
    private const int DefaultConfirmSeconds = 15;

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly HotkeyRecorder _toggleHotkeyRecorder;
    private readonly ILogger _log;
    private readonly Hotkey _allDisplaysOnHotkey = HotkeyService.AllDisplaysOnHotkey;
    private bool _loading;

    public SettingsViewModel(
        SettingsService settings, ProfileCatalog catalog, HotkeyService hotkeys, UsbDevicesViewModel devices, IUpdatePolicy updatePolicy, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(updatePolicy);
        ArgumentNullException.ThrowIfNull(log);
        ShowUpdateSetting = !updatePolicy.ChecksDisabled;
        _settings = settings;
        _catalog = catalog;
        _toggleHotkeyRecorder = new HotkeyRecorder(hotkeys, HotkeyUseKind.Toggle, Guid.Empty, "Settings_ToggleHotkeyHint");
        Devices = devices;
        _log = log.ForContext<SettingsViewModel>();

        // Texts built in code (hint, "None", "Same as Windows") follow a language change without a restart (I-13).
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ToggleHotkeyHint));
            OnPropertyChanged(nameof(AllDisplaysOnHotkeyText));
            Load();
        };
    }

    /// <summary>The device group: the USB devices rules and profiles use, and the pause switch.</summary>
    public UsbDevicesViewModel Devices { get; }

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    public ObservableCollection<Choice> LanguageChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedDefaultProfile { get; set; }

    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>
    /// Switch "confirm after switching". Stored only as <see cref="AppSettings.ConfirmTimeoutSeconds"/>: off is 0, on
    /// writes the seconds shown below, so there is no second value that could contradict it.
    /// </summary>
    [ObservableProperty]
    public partial bool ConfirmEnabled { get; set; }

    [ObservableProperty]
    public partial double? ConfirmTimeoutSeconds { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedLanguage { get; set; }

    /// <summary>"Back to the previous profile" (1.7.0); recorded like a profile hotkey in the editor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToggleHotkeyText), nameof(HasToggleHotkey))]
    public partial Hotkey? ToggleHotkey { get; set; }

    public string ToggleHotkeyText => ToggleHotkey is null ? string.Empty : HotkeyFormat.Format(ToggleHotkey);

    public bool HasToggleHotkey => ToggleHotkey is not null;

    /// <summary>The emergency hotkey, fixed (<see cref="HotkeyService.AllDisplaysOnHotkey"/>); in the language shown.</summary>
    public string AllDisplaysOnHotkeyText => HotkeyFormat.Format(_allDisplaysOnHotkey);

    /// <summary>The line under the hotkey field: what it does, or why the last combination was refused.</summary>
    public string ToggleHotkeyHint => _toggleHotkeyRecorder.Hint;

    /// <summary>The hotkey field took the focus: RigShift's own hotkeys must not fire while a combination is pressed.</summary>
    public void BeginHotkeyRecording() => _toggleHotkeyRecorder.Begin();

    public void EndHotkeyRecording() => _toggleHotkeyRecorder.End();

    /// <summary>A key combination pressed in the hotkey field; saved at once, like every setting on this page.</summary>
    public void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        if (_toggleHotkeyRecorder.Record(modifiers, virtualKey) is { } hotkey)
        {
            ToggleHotkey = hotkey;
            _log.Information("Toggle hotkey set to {Hotkey}", ToggleHotkeyText);
            Persist(s => s with { ToggleHotkey = hotkey });
        }

        OnPropertyChanged(nameof(ToggleHotkeyHint));
    }

    [RelayCommand]
    public void ClearHotkey()
    {
        ToggleHotkey = null;
        _toggleHotkeyRecorder.Reset();
        OnPropertyChanged(nameof(ToggleHotkeyHint));
        _log.Information("Toggle hotkey removed");
        Persist(s => s with { ToggleHotkey = null });
    }

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

            ConfirmEnabled = current.ConfirmTimeoutSeconds > 0;
            ConfirmTimeoutSeconds = current.ConfirmTimeoutSeconds > 0 ? current.ConfirmTimeoutSeconds : DefaultConfirmSeconds;
            InstallUpdatesAutomatically = !current.OnlyNotifyAboutUpdates;
            DetailedLogging = current.DetailedLogging;
            StartWithWindows = _settings.Autostart.IsEnabled;
            ToggleHotkey = current.ToggleHotkey;
            Devices.Refresh();
        }
        finally
        {
            _loading = false;
        }
    }

    [ObservableProperty]
    public partial bool InstallUpdatesAutomatically { get; set; }

    /// <summary>False when a policy switches update checks off: the choice would have no effect.</summary>
    public bool ShowUpdateSetting { get; }

    partial void OnInstallUpdatesAutomaticallyChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { OnlyNotifyAboutUpdates = !value });
        }
    }

    [ObservableProperty]
    public partial bool DetailedLogging { get; set; }

    partial void OnDetailedLoggingChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { DetailedLogging = value });
        }
    }

    partial void OnSelectedDefaultProfileChanged(Choice? value)
    {
        if (!_loading && value is not null)
        {
            Guid? id = value.Key is { } key ? Guid.Parse(key) : null;
            Persist(s => s with { DefaultProfileId = id });
        }
    }

    partial void OnConfirmEnabledChanged(bool value)
    {
        if (!_loading)
        {
            _log.Information("Confirmation after switching turned {State}", value ? "on" : "off");
            Persist(s => s with { ConfirmTimeoutSeconds = value ? EnabledConfirmSeconds() : 0 });
        }
    }

    partial void OnConfirmTimeoutSecondsChanged(double? value)
    {
        if (!_loading && ConfirmEnabled && value is not null)
        {
            Persist(s => s with { ConfirmTimeoutSeconds = EnabledConfirmSeconds() });
        }
    }

    /// <summary>While switched on, 0 would silently mean "off" again, so at least one second.</summary>
    private int EnabledConfirmSeconds() => (int)Math.Clamp(Math.Round(ConfirmTimeoutSeconds ?? DefaultConfirmSeconds), 1, 120);

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
