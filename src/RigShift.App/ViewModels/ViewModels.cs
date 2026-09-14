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
        double hertz = RefreshRate.Of(display).Hertz;
        string text = string.Create(Loc.Instance.Culture,
            $"{SwitchMessages.NameOf(display)} · {display.Width} × {display.Height} @ {hertz:0.##} Hz");
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
    private readonly ILogger _log = log.ForContext<ProfilesViewModel>();

    public ProfileCatalog Catalog => catalog;

    public SwitchCoordinator Coordinator => coordinator;

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
                file, executable, "apply " + RigShift.Core.Cli.CommandLineArguments.Quote(item.Name), Loc.Format("Shortcut_Description", item.Name));
            _log.Information("Shortcut {File} created for profile {Profile}", file, item.Name);
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            _log.Error(ex, "Shortcut {File} could not be created", file);
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
            _log.Error(ex, "Profile store action failed");
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Every switch result also on the profile page: Windows suppresses tray balloons while a full-screen game runs,
    /// which is exactly when RigShift switches (analysis finding I-04).
    /// </summary>
    public void ShowSwitchResult(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        (string title, string text, _) = SwitchMessages.ForNotification(record);
        ShowStatus(title + Environment.NewLine + text, record.Outcome switch
        {
            SwitchOutcome.Applied => Wpf.Ui.Controls.InfoBarSeverity.Success,
            SwitchOutcome.Failed => Wpf.Ui.Controls.InfoBarSeverity.Error,
            _ => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        });
    }

    private void ShowStatus(string message, Wpf.Ui.Controls.InfoBarSeverity severity = Wpf.Ui.Controls.InfoBarSeverity.Success)
    {
        // Closed first: after the user closed the bar with its X, setting true again must be a change (analysis finding I-02).
        IsStatusOpen = false;
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
}

public sealed record Choice(string? Key, string Name)
{
    /// <summary>Screen readers and type-ahead in combo boxes read the display name.</summary>
    public override string ToString() => Name;
}

public sealed partial class SettingsViewModel : ObservableObject
{
    private const int DefaultConfirmSeconds = 15;

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly ILogger _log;
    private bool _loading;

    public SettingsViewModel(SettingsService settings, ProfileCatalog catalog, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _catalog = catalog;
        _log = log.ForContext<SettingsViewModel>();

        // Texts built in code (hint, "None", "Same as Windows") follow a language change without a restart (I-13).
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ConfirmHint));
            Load();
        };
    }

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
    [NotifyPropertyChangedFor(nameof(ConfirmHint))]
    public partial bool ConfirmEnabled { get; set; }

    [ObservableProperty]
    public partial double? ConfirmTimeoutSeconds { get; set; }

    public string ConfirmHint => Loc.Instance[ConfirmEnabled ? "Settings_ConfirmTimeoutHint" : "Settings_ConfirmOffHint"];

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

            ConfirmEnabled = current.ConfirmTimeoutSeconds > 0;
            ConfirmTimeoutSeconds = current.ConfirmTimeoutSeconds > 0 ? current.ConfirmTimeoutSeconds : DefaultConfirmSeconds;
            InstallUpdatesAutomatically = !current.OnlyNotifyAboutUpdates;
            StartWithWindows = _settings.Autostart.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    [ObservableProperty]
    public partial bool InstallUpdatesAutomatically { get; set; }

    partial void OnInstallUpdatesAutomaticallyChanged(bool value)
    {
        if (!_loading)
        {
            Persist(s => s with { OnlyNotifyAboutUpdates = !value });
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

/// <summary>The monitors attached right now, with their custom names and a way to tell them apart (docs/PLAN.md, section 6).</summary>
public sealed partial class DisplaysViewModel(IDisplayConfigurator display, ProfileCatalog catalog, ILogger log) : ObservableObject
{
    private readonly ILogger _log = log.ForContext<DisplaysViewModel>();

    public ObservableCollection<DisplayCard> Displays { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    internal async Task RenameAsync(DisplayCard card, string? name)
    {
        try
        {
            await catalog.RenameDisplayAsync(card.TargetDevicePath, name, CancellationToken.None);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Display {Display} could not be renamed", card.ModelName);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => display.QueryAsync(CancellationToken.None));
            IReadOnlyDictionary<string, string> names = catalog.KnownDisplayNames;
            Displays.Clear();
            int number = 0;
            foreach (AttachedDisplay attached in snapshot.Displays
                .OrderByDescending(d => d.IsActive)
                .ThenBy(d => d.ActiveMode?.PositionX ?? int.MaxValue)
                .ThenBy(d => d.ActiveMode?.PositionY ?? 0))
            {
                int? shown = attached.IsActive && attached.ActiveMode is not null ? ++number : null;
                Displays.Add(new DisplayCard(this, attached, shown, names.GetValueOrDefault(attached.Identity.TargetDevicePath), ProfilesWith(attached)));
            }

            IsEmpty = Displays.Count == 0;
            ErrorMessage = null;
            _log.Information("Displays page shows {Count} displays", Displays.Count);
        }
        catch (Win32Exception ex)
        {
            _log.Error(ex, "Displays could not be read");
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    private void Identify()
    {
        var shown = Displays
            .Where(d => d.Number is not null && d.Mode is not null)
            .Select(d => (d.Number!.Value, d.Name, d.Mode!))
            .ToList();
        _log.Information("Identifying {Count} displays", shown.Count);
        Views.IdentifyWindow.ShowAll(shown);
    }

    private string ProfilesWith(AttachedDisplay attached)
    {
        List<string> names = catalog.Profiles
            .Where(p => p.Displays.Any(d => string.Equals(d.Identity.TargetDevicePath, attached.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase)))
            .Select(p => p.Name)
            .ToList();
        return names.Count == 0 ? Loc.Instance["Displays_NotInProfiles"] : Loc.Format("Displays_InProfiles", string.Join(", ", names));
    }
}

/// <summary>One attached monitor on the displays page. The name is saved when the field loses focus or on Enter.</summary>
public sealed partial class DisplayCard : ObservableObject
{
    private readonly DisplaysViewModel _owner;
    private readonly DisplayIdentity _identity;
    private string? _savedName;

    public DisplayCard(DisplaysViewModel owner, AttachedDisplay display, int? number, string? customName, string profilesText)
    {
        ArgumentNullException.ThrowIfNull(display);

        _owner = owner;
        _identity = display.Identity;
        _savedName = DisplayNames.Normalize(customName);
        CustomName = _savedName ?? string.Empty;
        Number = number;
        Mode = display.ActiveMode;
        ProfilesText = profilesText;

        string state = display.IsActive
            ? Loc.Instance[display.ActiveMode?.IsPrimary == true ? "Displays_StatePrimary" : "Displays_StateActive"]
            : Loc.Instance[display.IsAvailable ? "Displays_StateOff" : "Displays_StateNotReady"];
        DetailsText = Mode is { } mode
            ? state + " · " + Loc.Format("Displays_Mode", mode.Width, mode.Height,
                RefreshRate.Of(mode).Hertz.ToString("0.##", Loc.Instance.Culture))
            : state;
    }

    public string TargetDevicePath => _identity.TargetDevicePath;

    /// <summary>Left-to-right number of an active display, as shown by "Identify".</summary>
    public int? Number { get; }

    public string NumberText => Number?.ToString(Loc.Instance.Culture) ?? "–";

    public DisplayAssignment? Mode { get; }

    public string ModelName => SwitchMessages.NameOf(null, _identity);

    public string Name => SwitchMessages.NameOf(DisplayNames.Normalize(CustomName), _identity);

    public string DetailsText { get; }

    public string ProfilesText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string CustomName { get; set; }

    internal async Task SaveNameAsync()
    {
        string? name = DisplayNames.Normalize(CustomName);
        if (name == _savedName)
        {
            return;
        }

        _savedName = name;
        await _owner.RenameAsync(this, name);
    }
}

/// <summary>Version and updates, troubleshooting and links (docs/PLAN.md, section 6).</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly UpdateService _updates;
    private readonly SwitchCoordinator _coordinator;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly AppPaths _paths;
    private readonly ILogger _log;

    public AboutViewModel(
        UpdateService updates, SwitchCoordinator coordinator, ProfileCatalog catalog, IDisplayConfigurator display, IAudioController audio, AppPaths paths, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(log);

        _updates = updates;
        _coordinator = coordinator;
        _catalog = catalog;
        _display = display;
        _audio = audio;
        _paths = paths;
        _log = log.ForContext<AboutViewModel>();
        _updates.StateChanged += (_, _) => RefreshUpdateStatus();
        _coordinator.History.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoHistory));
        RefreshUpdateStatus();

        // Version, update status and the history rows are built in code; re-read them on a language change (I-13).
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VersionText));
            RefreshUpdateStatus();
            CopyStatus = null;
            System.Windows.Data.CollectionViewSource.GetDefaultView(History).Refresh();
        };
    }

    public string VersionText => Loc.Format(_updates.IsInstalled ? "Settings_Version" : "Settings_VersionDev", _updates.CurrentVersion);

    [ObservableProperty]
    public partial string UpdateStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsUpdateInstallable { get; set; }

    /// <summary>Release notes and the link to the release, shown while a newer version is known.</summary>
    [ObservableProperty]
    public partial bool ShowReleaseNotes { get; set; }

    /// <summary>Shown collapsed under "What's new", so a long changelog section does not blow up the card.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<RigShift.Core.Updates.ReleaseNoteLine> ReleaseNoteLines { get; set; } = [];

    public ObservableCollection<SwitchRecord> History => _coordinator.History;

    public bool HasNoHistory => History.Count == 0;

    [ObservableProperty]
    public partial string? CopyStatus { get; set; }

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private Task CheckForUpdatesAsync() => _updates.CheckNowAsync();

    private bool CanCheckForUpdates() => _updates.CanCheck;

    [RelayCommand(CanExecute = nameof(CanInstallUpdateNow))]
    private Task InstallUpdateNowAsync() => _updates.InstallNowAsync();

    private bool CanInstallUpdateNow() => _updates.CanInstallNow;

    [RelayCommand]
    private void OpenReleaseNotes() => ShellFolders.OpenUrl(_updates.ReleaseUrl, _log);

    [RelayCommand]
    private void OpenLogFolder() => ShellFolders.Open(_paths.Logs, _log);

    /// <summary>Next to the log folder: the settings page holds only settings (user decision O-06).</summary>
    [RelayCommand]
    private void OpenProfileFolder() => ShellFolders.Open(_paths.Profiles, _log);

    [RelayCommand]
    private void OpenRepository() => ShellFolders.OpenUrl(RepositoryUrl, _log);

    [RelayCommand]
    private void ReportProblem() => ShellFolders.OpenUrl(RepositoryUrl + "/issues/new", _log);

    [RelayCommand]
    private void OpenLicense() => ShellFolders.OpenUrl(RepositoryUrl + "/blob/main/LICENSE", _log);

    /// <summary>Voluntary donations; only a link next to the others, never a prompt.</summary>
    [RelayCommand]
    private void OpenKofi() => ShellFolders.OpenUrl("https://ko-fi.com/filthyjoker", _log);

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        DisplaySnapshot? snapshot = null;
        string? displayError = null;
        try
        {
            snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Displays could not be read for the diagnostic report");
            displayError = ex.Message;
        }

        IReadOnlyList<AudioDeviceInfo> playback = [];
        IReadOnlyList<AudioDeviceInfo> recording = [];
        string? audioError = null;
        try
        {
            playback = await Task.Run(() => _audio.ListAsync(AudioDirection.Render, CancellationToken.None));
            recording = await Task.Run(() => _audio.ListAsync(AudioDirection.Capture, CancellationToken.None));
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.Warning(ex, "Audio devices could not be read for the diagnostic report");
            audioError = ex.Message;
        }

        string report = DiagnosticsReport.Build(new DiagnosticsInput(
            _updates.CurrentVersion, _updates.IsInstalled, snapshot, displayError, playback, recording, audioError,
            _catalog.Profiles, _catalog.ActiveProfile?.Id, _catalog.KnownDisplayNames, [.. History]));

        try
        {
            System.Windows.Clipboard.SetText(report);
            CopyStatus = Loc.Instance["About_Copied"];
            _log.Information("Diagnostic report copied to the clipboard ({Length} characters)", report.Length);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Diagnostic report could not be copied to the clipboard");
            CopyStatus = Loc.Format("About_CopyFailed", ex.Message);
        }
    }

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
            UpdateState.Ready => Loc.Format("Update_StatusReady", _updates.TargetVersion ?? "?"),
            UpdateState.Available => Loc.Format("Update_StatusAvailable", _updates.TargetVersion ?? "?"),
            _ => Loc.Instance["Update_StatusFailed"],
        };
        IsUpdateInstallable = _updates.State is UpdateState.Ready or UpdateState.Available;
        ShowReleaseNotes = _updates.State is UpdateState.Ready or UpdateState.Available or UpdateState.Downloading && _updates.ReleaseUrl is not null;
        ReleaseNoteLines = _updates.ReleaseNoteLines;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateNowCommand.NotifyCanExecuteChanged();
    }
}
