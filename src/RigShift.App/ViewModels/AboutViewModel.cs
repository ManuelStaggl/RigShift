using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>Version and updates, troubleshooting and links.</summary>
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
    private readonly ILogger _log;

    public AboutViewModel(
        UpdateService updates,
        SwitchCoordinator coordinator,
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IAudioController audio,
        AppPaths paths,
        SettingsService settings,
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
        _log = log.ForContext<AboutViewModel>();
        _updates.StateChanged += (_, _) => RefreshUpdateStatus();
        RefreshUpdateStatus();

        // Version, update status and the history rows are built in code; re-read them on a language change (I-13).
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(VersionText));
            RefreshUpdateStatus();
            CopyStatus = null;
            BackupStatus = null;
            System.Windows.Data.CollectionViewSource.GetDefaultView(History).Refresh();
        };
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
            BackupStatus = Loc.Format("About_BackupFailed", ex.Message);
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
            BackupStatus = Loc.Format("About_BackupFailed", ex.Message);
            return;
        }

        if (!await ProfileDialogs.ConfirmRestoreAsync(content.Profiles.Count))
        {
            return;
        }

        if (_coordinator.IsSwitching)
        {
            BackupStatus = Loc.Instance["About_BackupBusy"];
            return;
        }

        try
        {
            await Task.Run(() => BackupArchive.Restore(_paths.DataDirectory, content, _log));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Backup {File} could not be restored", dialog.FileName);
            BackupStatus = Loc.Format("About_BackupFailed", ex.Message);
        }

        // Even after a failure halfway, what is on disk now is what counts.
        await _settings.ReloadAsync(CancellationToken.None);
        await _catalog.ReloadAsync(CancellationToken.None);
        if (BackupStatus is null || !BackupStatus.StartsWith(Loc.Format("About_BackupFailed", string.Empty), StringComparison.Ordinal))
        {
            BackupStatus = Loc.Format("About_BackupRestored", content.Profiles.Count);
        }
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
