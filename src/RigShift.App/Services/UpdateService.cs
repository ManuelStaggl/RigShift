using System.Reflection;
using Microsoft.Win32;
using RigShift.Core.Updates;
using Serilog;
using Velopack;

namespace RigShift.App.Services;

public enum UpdateState
{
    /// <summary>Not installed by Velopack (development build); updates are off.</summary>
    NotInstalled,

    /// <summary>An administrator switched update checks off (<see cref="IUpdatePolicy"/>); GitHub is never contacted.</summary>
    DisabledByPolicy,

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
/// Checks GitHub Releases at startup, every 24 hours, after waking from standby and on request; a failed check is tried
/// again after 1, 5 and 30 minutes (<see cref="UpdateSchedule"/>). With automatic
/// installation a newer version is downloaded and Velopack installs it the next time the tray app starts; otherwise
/// RigShift only reports it until the user installs it. Does nothing when RigShift was not installed by Velopack
/// (development builds) or when a policy switches the checks off. All members are used on the UI thread; events are raised there.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly IUpdateFeed _feed;
    private readonly CancellationTokenSource _stop = new();
    private readonly SwitchCoordinator _coordinator;
    private readonly SettingsService _settings;
    private readonly IAppShell _shell;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private UpdateInfo? _available;
    private VelopackAsset? _pending;
    private bool _busy;
    private int _failuresInARow;
    private DateTimeOffset? _lastSuccess;

    /// <summary>Completed to end the wait for the next check early – after waking from standby.</summary>
    private volatile TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public UpdateService(
        IUpdateFeed feed, IUpdatePolicy policy, SwitchCoordinator coordinator, SettingsService settings, IAppShell shell, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(feed);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);
        _feed = feed;
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

        IsInstalled = _feed.IsInstalled;
        DisabledByPolicy = policy.ChecksDisabled;
        State = !IsInstalled ? UpdateState.NotInstalled
            : DisabledByPolicy ? UpdateState.DisabledByPolicy
            : UpdateState.NotChecked;
        CurrentVersion = _feed.InstalledVersion is { } installed
            ? installed
            : Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary>Raised on the UI thread with the version that is downloaded and ready to install.</summary>
    public event EventHandler<string>? UpdateReady;

    /// <summary>Raised on the UI thread with a newer version that is not downloaded (automatic installation off).</summary>
    public event EventHandler<string>? UpdateAvailable;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or its details change.</summary>
    public event EventHandler? StateChanged;

    public bool IsInstalled { get; }

    public bool DisabledByPolicy { get; }

    public string CurrentVersion { get; }

    public UpdateState State { get; private set; }

    /// <summary>Version that is available, being downloaded or ready to install.</summary>
    public string? TargetVersion { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    /// <summary>Release notes of the newest version found, as compact lines; empty when the release has none.</summary>
    public IReadOnlyList<ReleaseNoteLine> ReleaseNoteLines { get; private set; } = [];

    /// <summary>GitHub release page of <see cref="TargetVersion"/>.</summary>
    public string? ReleaseUrl => TargetVersion is null ? null : RepositoryUrl + "/releases/tag/v" + TargetVersion;

    public bool CanCheck => IsInstalled && !DisabledByPolicy && !_busy;

    /// <summary>An update can be installed now; never while checking, downloading or switching.</summary>
    public bool CanInstallNow => State is UpdateState.Ready or UpdateState.Available && !_busy && !_coordinator.IsSwitching;

    public void Start()
    {
        if (!IsInstalled)
        {
            _log.Information("Not installed by Velopack, update checks are off");
            return;
        }

        if (DisabledByPolicy)
        {
            return;
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
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
            _feed.ApplyAfterExit(_pending);
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
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                await CheckAsync();

                // The check at login often runs before the network is up; waiting a day for the next one meant a PC
                // that is switched off every night never saw an update.
                TimeSpan wait = UpdateSchedule.NextCheckIn(_failuresInARow);
                if (_failuresInARow > 0)
                {
                    _log.Information("Update check failed {Failures} time(s) in a row, next try in {Wait}", _failuresInARow, wait);
                }

                await WaitAsync(wait);
            }
        }
        catch (OperationCanceledException)
        {
            // App is exiting.
        }
    }

    /// <summary>Waits for the next check; waking from standby ends the wait when a check is due by then.</summary>
    private async Task WaitAsync(TimeSpan wait)
    {
        Task delay = Task.Delay(wait, _time, _stop.Token);
        while (true)
        {
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (await Task.WhenAny(delay, _wake.Task) == delay)
            {
                await delay;
                return;
            }

            if (UpdateSchedule.IsDueAfterResume(_lastSuccess, _time.GetUtcNow(), _failuresInARow))
            {
                _log.Information("Resumed from sleep, checking for updates now");
                return;
            }
        }
    }

    // SystemEvents raises on its own thread; completing the task hands over to the loop on the UI thread.
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Resumed();
        }
    }

    /// <summary>The PC woke from standby; ends the wait for the next check when one is due.</summary>
    internal void Resumed() => _wake.TrySetResult();

    private async Task CheckAsync()
    {
        if (!CanCheck)
        {
            return;
        }

        _busy = true;
        string? readyVersion = State == UpdateState.Ready ? TargetVersion : null;

        // Read before the state turns to Checking: a version that was reported already is not reported every day.
        string? reportedVersion = State == UpdateState.Available ? TargetVersion : null;
        SetState(readyVersion is null ? UpdateState.Checking : UpdateState.Ready, readyVersion);
        UpdateInfo? update;
        try
        {
            update = await _feed.CheckAsync();
            LastChecked = _time.GetLocalNow();
            _lastSuccess = LastChecked;
            _failuresInARow = 0;
        }
        catch (Exception ex)
        {
            _failuresInARow++;
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
            bool isNew = version != reportedVersion;
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
            await _feed.DownloadAsync(update, _stop.Token);
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
