using System.Reflection;
using RigShift.Core.Updates;
using Serilog;
using Velopack;
using Velopack.Sources;

namespace RigShift.App.Services;

public enum UpdateState
{
    /// <summary>Not installed by Velopack (development build); updates are off.</summary>
    NotInstalled,

    /// <summary>Installed, but no check has finished yet.</summary>
    NotChecked,

    Checking,

    /// <summary>A newer version exists; automatic installation is off, so nothing is downloaded yet.</summary>
    Available,

    Downloading,
    UpToDate,

    /// <summary>A newer version is downloaded and installed on the next start.</summary>
    Ready,

    Failed,
}

/// <summary>
/// Checks GitHub Releases at startup, every 24 hours and on request. With automatic
/// installation a newer version is downloaded and Velopack installs it the next time the tray app starts; otherwise
/// RigShift only reports it until the user installs it. Does nothing when RigShift was not installed by Velopack
/// (development builds). All members are used on the UI thread; events are raised there.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private readonly PeriodicTimer _timer = new(TimeSpan.FromHours(24));
    private readonly CancellationTokenSource _stop = new();
    private readonly SwitchCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly IAppShell _shell;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private UpdateInfo? _available;
    private VelopackAsset? _pending;
    private bool _busy;

    public UpdateService(SwitchCoordinator coordinator, SettingsService settings, IAppShell shell, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _coordinator = coordinator;
        _settings = settings;
        _shell = shell;
        _time = time;
        _log = log.ForContext<UpdateService>();
        _coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching))
            {
                StateChanged?.Invoke(this, EventArgs.Empty);
            }
        };

        // Turning automatic installation on while a version is only reported downloads it right away.
        _settings.Changed += async (_, _) =>
        {
            if (!_settings.Current.OnlyNotifyAboutUpdates && State == UpdateState.Available)
            {
                await CheckAsync();
            }
        };

        IsInstalled = _manager.IsInstalled;
        State = IsInstalled ? UpdateState.NotChecked : UpdateState.NotInstalled;
        CurrentVersion = IsInstalled && _manager.CurrentVersion is { } installed
            ? installed.ToString()
            : Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary>Raised on the UI thread with the version that is downloaded and ready to install.</summary>
    public event EventHandler<string>? UpdateReady;

    /// <summary>Raised on the UI thread with a newer version that is not downloaded (automatic installation off).</summary>
    public event EventHandler<string>? UpdateAvailable;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or its details change.</summary>
    public event EventHandler? StateChanged;

    public bool IsInstalled { get; }

    public string CurrentVersion { get; }

    public UpdateState State { get; private set; }

    /// <summary>Version that is available, being downloaded or ready to install.</summary>
    public string? TargetVersion { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Release notes of the newest version found, as compact lines; empty when the release has none.</summary>
    public IReadOnlyList<ReleaseNoteLine> ReleaseNoteLines { get; private set; } = [];

    /// <summary>GitHub release page of <see cref="TargetVersion"/>.</summary>
    public string? ReleaseUrl => TargetVersion is null ? null : RepositoryUrl + "/releases/tag/v" + TargetVersion;

    public bool CanCheck => IsInstalled && !_busy;

    /// <summary>An update can be installed now; never while checking, downloading or switching.</summary>
    public bool CanInstallNow => State is UpdateState.Ready or UpdateState.Available && !_busy && !_coordinator.IsSwitching;

    public void Start()
    {
        if (!IsInstalled)
        {
            _log.Information("Not installed by Velopack, update checks are off");
            return;
        }

        _ = RunAsync();
    }

    /// <summary>Checks now, unless a check is already running or updates are off.</summary>
    public Task CheckNowAsync()
    {
        _log.Information("Update check requested by the user");
        return CheckAsync();
    }

    /// <summary>
    /// Downloads the update if needed, hands it to the Velopack updater, which waits for RigShift to exit, installs and
    /// starts it again, and exits cleanly so the tray icon and the log are closed.
    /// </summary>
    public async Task InstallNowAsync()
    {
        if (!CanInstallNow)
        {
            _log.Warning("Install now refused: state {State}, busy {Busy}, switching {Switching}", State, _busy, _coordinator.IsSwitching);
            return;
        }

        if (State == UpdateState.Available && _available is { } update)
        {
            _log.Information("Installing reported update {Version} on request", TargetVersion);
            if (!await DownloadAsync(update))
            {
                return;
            }
        }

        if (_pending is null)
        {
            return;
        }

        try
        {
            _log.Information("Restarting to install update {Version}", _pending.Version);
            _manager.WaitExitThenApplyUpdates(_pending, silent: true, restart: true);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Could not start the updater for {Version}", _pending.Version);
            SetState(UpdateState.Failed, null);
            return;
        }

        _shell.Quit();
    }

    public void Dispose()
    {
        _stop.Cancel();
        _timer.Dispose();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            do
            {
                await CheckAsync();
            }
            while (await _timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException)
        {
            // App is exiting.
        }
    }

    private async Task CheckAsync()
    {
        if (!CanCheck)
        {
            return;
        }

        _busy = true;
        string? readyVersion = State == UpdateState.Ready ? TargetVersion : null;
        SetState(readyVersion is null ? UpdateState.Checking : UpdateState.Ready, readyVersion);
        UpdateInfo? update;
        try
        {
            update = await _manager.CheckForUpdatesAsync();
            LastChecked = _time.GetLocalNow();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Update check failed");
            SetState(readyVersion is null ? UpdateState.Failed : UpdateState.Ready, readyVersion);
            _busy = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _busy = false;
        if (update is null)
        {
            _log.Information("No update available, installed version {Version}", CurrentVersion);
            ReleaseNoteLines = [];
            SetState(UpdateState.UpToDate, null);
            return;
        }

        string version = update.TargetFullRelease.Version.ToString();
        ReleaseNoteLines = ReleaseNotes.Parse(update.TargetFullRelease.NotesMarkdown);
        if (version == readyVersion)
        {
            SetState(UpdateState.Ready, version);
            return;
        }

        if (_settings.Current.OnlyNotifyAboutUpdates)
        {
            bool isNew = !(State == UpdateState.Available && TargetVersion == version) && version != readyVersion;
            _available = update;
            _log.Information("Update {Version} available, automatic installation is off", version);
            SetState(UpdateState.Available, version);
            if (isNew)
            {
                UpdateAvailable?.Invoke(this, version);
            }

            return;
        }

        if (await DownloadAsync(update))
        {
            UpdateReady?.Invoke(this, version);
        }
    }

    private async Task<bool> DownloadAsync(UpdateInfo update)
    {
        string version = update.TargetFullRelease.Version.ToString();
        _busy = true;
        try
        {
            _log.Information("Downloading update {Version}", version);
            SetState(UpdateState.Downloading, version);
            await _manager.DownloadUpdatesAsync(update, cancelToken: _stop.Token);
            _pending = update.TargetFullRelease;
            _available = null;
            _log.Information("Update {Version} downloaded, it is installed on the next start", version);
            _busy = false;
            SetState(UpdateState.Ready, version);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Downloading update {Version} failed", version);
            _busy = false;
            SetState(UpdateState.Failed, null);
            return false;
        }
    }

    private void SetState(UpdateState state, string? targetVersion)
    {
        State = state;
        TargetVersion = targetVersion;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
