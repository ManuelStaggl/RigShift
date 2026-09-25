using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using RigShift.Windows.Display;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>The help page: version and updates, first steps, the hotkeys and commands in use, backup, troubleshooting.</summary>
public sealed partial class AboutViewModel : ObservableObject
{
    private const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly UpdateService _updates;
    private readonly SwitchCoordinator _coordinator;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly AppPaths _paths;
    private readonly SettingsService _settings;
    private readonly GameCatalog _games;
    private readonly ProfileDialogs _dialogs;
    private readonly ILogger _log;
    private readonly UiThread _ui = new();

    public AboutViewModel(
        UpdateService updates,
        SwitchCoordinator coordinator,
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IAudioController audio,
        AppPaths paths,
        SettingsService settings,
        GameCatalog games,
        ProfileDialogs dialogs,
        ILogger log)
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
        _settings = settings;
        _games = games;
        _dialogs = dialogs;
        _log = log.ForContext<AboutViewModel>();
        catalog.ProfilesChanged += (_, _) => RebuildShortcuts();
        games.Changed += (_, _) => RebuildShortcuts();
        settings.Changed += (_, _) => RebuildShortcuts();
        RebuildShortcuts();
        _updates.StateChanged += (_, _) => RefreshUpdateStatus();
        RefreshUpdateStatus();

        // Version, update status and the history rows are built in code; re-read them on a language change (I-13).
        Loc.Instance.PropertyChanged += (_, _) => _ui.Run(() =>
        {
            OnPropertyChanged(nameof(VersionText));
            RefreshUpdateStatus();
            CopyStatus = null;
            BackupStatus = null;
            System.Windows.Data.CollectionViewSource.GetDefaultView(History).Refresh();
            RebuildShortcuts();
        });
    }

    [ObservableProperty]
    public partial string? BackupStatus { get; set; }

    /// <summary>Profiles, rules and settings as one ZIP file (1.7.0): for a new PC or after a reinstall.</summary>
    [RelayCommand]
    private async Task SaveBackupAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"RigShift-backup-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            int count = await Task.Run(() =>
            {
                using FileStream stream = File.Create(dialog.FileName);
                return BackupArchive.Write(_paths.DataDirectory, stream);
            });
            _log.Information("Backup with {Count} profile(s) saved to {File}", count, dialog.FileName);
            BackupStatus = Loc.Format("About_BackupSaved", count);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Backup could not be saved to {File}", dialog.FileName);
            BackupStatus = Loc.Format("About_BackupFailed", UserMessages.Describe(ex));
        }
    }

    /// <summary>Replaces every profile and the settings after a confirmation; the running app picks the new files up.</summary>
    [RelayCommand]
    private async Task RestoreBackupAsync()
    {
        if (_coordinator.IsSwitching)
        {
            BackupStatus = Loc.Instance["About_BackupBusy"];
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "ZIP (*.zip)|*.zip", CheckFileExists = true };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        BackupContent content;
        try
        {
            content = await Task.Run(() =>
            {
                using FileStream stream = File.OpenRead(dialog.FileName);
                return BackupArchive.Inspect(stream);
            });
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Backup {File} could not be read", dialog.FileName);
            BackupStatus = Loc.Format("About_BackupFailed", UserMessages.Describe(ex));
            return;
        }

        if (!await ProfileDialogs.ConfirmRestoreAsync(content))
        {
            return;
        }

        if (_coordinator.IsSwitching)
        {
            BackupStatus = Loc.Instance["About_BackupBusy"];
            return;
        }

        bool failed = false;
        try
        {
            await Task.Run(() => BackupArchive.Restore(_paths.DataDirectory, content, _log));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Backup {File} could not be restored", dialog.FileName);
            BackupStatus = Loc.Format("About_BackupFailed", UserMessages.Describe(ex));
            failed = true;
        }

        // Even after a failure halfway, what is on disk now is what counts.
        await _settings.ReloadAsync(CancellationToken.None);
        await _catalog.ReloadAsync(CancellationToken.None);
        await _games.ReloadAsync(CancellationToken.None);
        if (!failed)
        {
            BackupStatus = Loc.Format("About_BackupRestored", content.Profiles.Count);
        }
    }

    /// <summary>The hotkeys in use: profiles, games and "previous profile" (H-01).</summary>
    public ObservableCollection<ShortcutRow> Shortcuts { get; } = [];

    [ObservableProperty]
    public partial bool HasShortcuts { get; set; }

    /// <summary>The commands for Stream Deck and scripts, with a placeholder for the name.</summary>
    public IReadOnlyList<string> CommandExamples { get; } = ["rigshift://apply/<name>", "rigshift://play/<name>", "rigshift://toggle"];

    /// <summary>Colour of the update line in the head: up to date is ok, an update waiting is the accent.</summary>
    [ObservableProperty]
    public partial StatusKind UpdateStatusKind { get; set; }

    [RelayCommand]
    private Task StartAssistantAsync() => _dialogs.ShowSetupAssistantAsync();

    [RelayCommand]
    private void OpenGuide() => ShellFolders.OpenUrl(RepositoryUrl + "#readme", _log);

    [RelayCommand]
    private void CopyCommand(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
            CopyStatus = Loc.Instance["About_Copied"];
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Command could not be copied to the clipboard");
            CopyStatus = Loc.Format("About_CopyFailed", UserMessages.Describe(ex));
        }
    }

    private void RebuildShortcuts()
    {
        Shortcuts.Clear();
        foreach (ProfileItem item in _catalog.Items.Where(i => i.HotkeyText.Length > 0))
        {
            Shortcuts.Add(new ShortcutRow(item.Name, item.HotkeyText));
        }

        foreach (GameItem item in _games.Items.Where(i => i.HotkeyText.Length > 0))
        {
            Shortcuts.Add(new ShortcutRow(item.Name, item.HotkeyText));
        }

        if (_settings.Current.ToggleHotkey is { } toggle)
        {
            Shortcuts.Add(new ShortcutRow(Loc.Instance["Settings_ToggleHotkey"], HotkeyFormat.Format(toggle)));
        }

        HasShortcuts = Shortcuts.Count > 0;

        // Always listed: the emergency hotkey has to be known before a screen stays dark.
        Shortcuts.Add(new ShortcutRow(Loc.Instance["Settings_AllOnHotkey"], HotkeyFormat.Format(HotkeyService.AllDisplaysOnHotkey)));

        // The keys inside the window (v4 finding U-24).
        Shortcuts.Add(new ShortcutRow(Loc.Instance["Help_KeysPages"], Ctrl(0x31) + " … " + Ctrl(0x36)));
        Shortcuts.Add(new ShortcutRow(Loc.Instance["Help_KeysEdit"], string.Join(" · ", Ctrl(0x4E), Ctrl(0x53), Ctrl(0x0D))));
        Shortcuts.Add(new ShortcutRow(Loc.Instance["Help_KeysName"], "F2"));
    }

    private static string Ctrl(int virtualKey) =>
        HotkeyFormat.Format(new Core.Profiles.Hotkey { Modifiers = Core.Profiles.HotkeyModifiers.Control, VirtualKey = virtualKey });

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

    /// <summary>The recent switches go into the diagnostic report; the page for reading them is the overview.</summary>
    private ObservableCollection<SwitchRecord> History => _coordinator.History;

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
    private void ReportProblem() => ShellFolders.OpenUrl(IssueUrl(_updates.CurrentVersion), _log);

    /// <summary>The bug report form with the version filled in.</summary>
    internal static string IssueUrl(string version) =>
        RepositoryUrl + "/issues/new?template=bug_report.yml&version=" + Uri.EscapeDataString(version);

    [RelayCommand]
    private void OpenLicense() => ShellFolders.OpenUrl(RepositoryUrl + "/blob/main/LICENSE", _log);

    [RelayCommand]
    private void OpenThirdPartyNotices() => ShellFolders.OpenUrl(RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md", _log);

    [RelayCommand]
    private void OpenPrivacy() => ShellFolders.OpenUrl(RepositoryUrl + "/blob/main/PRIVACY.md", _log);

    /// <summary>Voluntary donations; only a link next to the others, never a prompt.</summary>
    [RelayCommand]
    private void OpenKofi() => ShellFolders.OpenUrl("https://ko-fi.com/filthyjoker", _log);

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        string report = await BuildReportAsync();
        try
        {
            System.Windows.Clipboard.SetText(report);
            CopyStatus = Loc.Instance["About_Copied"];
            _log.Information("Diagnostic report copied to the clipboard ({Length} characters)", report.Length);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Diagnostic report could not be copied to the clipboard");
            CopyStatus = Loc.Format("About_CopyFailed", UserMessages.Describe(ex));
        }
    }

    /// <summary>Diagnostics, logs and data files as one ZIP, then the bug report form to attach it to (v4 finding E-07).</summary>
    [RelayCommand]
    private async Task SaveSupportPackageAsync()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = $"RigShift-support-{DateTime.Now:yyyy-MM-dd}.zip",
            DefaultExt = ".zip",
            Filter = "ZIP (*.zip)|*.zip",
            AddExtension = true,
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        string report = await BuildReportAsync();
        try
        {
            int count = await Task.Run(() =>
            {
                using FileStream stream = File.Create(dialog.FileName);
                return SupportPackage.Write(stream, report, _paths, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            });
            _log.Information("Support package with {Count} file(s) saved to {File}", count, dialog.FileName);
            CopyStatus = Loc.Instance["About_SupportSaved"];
            ShellFolders.OpenUrl(IssueUrl(_updates.CurrentVersion), _log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Support package could not be saved to {File}", dialog.FileName);
            CopyStatus = Loc.Format("About_SupportFailed", UserMessages.Describe(ex));
        }
    }

    internal async Task<string> BuildReportAsync()
    {
        DisplaySnapshot? snapshot = null;
        string? displayError = null;
        try
        {
            snapshot = await _display.QueryAsync(CancellationToken.None);
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
            playback = await _audio.ListAsync(AudioDirection.Render, CancellationToken.None);
            recording = await _audio.ListAsync(AudioDirection.Capture, CancellationToken.None);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            _log.Warning(ex, "Audio devices could not be read for the diagnostic report");
            audioError = ex.Message;
        }

        return DiagnosticsReport.Build(new DiagnosticsInput(
            _updates.CurrentVersion, _updates.IsInstalled, snapshot, displayError, playback, recording, audioError,
            _catalog.Profiles, _catalog.ActiveProfile?.Id, _catalog.KnownDisplayNames, [.. History],
            GraphicsDrivers.Read(), _settings.Current, _games.Items.Count, TextScale.Setting()));
    }

    private void RefreshUpdateStatus()
    {
        string? lastChecked = _updates.LastChecked?.ToString("g", Loc.Instance.Culture);
        UpdateStatusText = _updates.State switch
        {
            UpdateState.NotInstalled => Loc.Instance["Update_StatusNotInstalled"],
            UpdateState.DisabledByPolicy => Loc.Instance["Update_StatusPolicy"],
            UpdateState.NotChecked => Loc.Instance["Update_StatusNotChecked"],
            UpdateState.Checking => Loc.Instance["Update_StatusChecking"],
            UpdateState.Downloading => Loc.Format("Update_StatusDownloading", _updates.TargetVersion ?? "?"),
            UpdateState.UpToDate => Loc.Format("Update_StatusUpToDate", lastChecked ?? "?"),
            UpdateState.Ready => Loc.Format("Update_StatusReady", _updates.TargetVersion ?? "?"),
            UpdateState.Available => Loc.Format("Update_StatusAvailable", _updates.TargetVersion ?? "?"),
            _ => Loc.Instance["Update_StatusFailed"],
        };
        UpdateStatusKind = _updates.State switch
        {
            UpdateState.UpToDate => StatusKind.Ok,
            UpdateState.Ready or UpdateState.Available or UpdateState.Downloading => StatusKind.Accent,
            UpdateState.NotInstalled or UpdateState.DisabledByPolicy or UpdateState.NotChecked or UpdateState.Checking => StatusKind.Neutral,
            _ => StatusKind.Warn,
        };
        IsUpdateInstallable = _updates.State is UpdateState.Ready or UpdateState.Available;
        ShowReleaseNotes = _updates.State is UpdateState.Ready or UpdateState.Available or UpdateState.Downloading && _updates.ReleaseUrl is not null;
        ReleaseNoteLines = _updates.ReleaseNoteLines;
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        InstallUpdateNowCommand.NotifyCanExecuteChanged();
    }
}

/// <summary>One hotkey on the help page: what it does and the keys.</summary>
public sealed record ShortcutRow(string Name, string Keys);
