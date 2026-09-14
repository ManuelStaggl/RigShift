using System.Reflection;
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
    Downloading,
    UpToDate,

    /// <summary>A newer version is downloaded and installed on the next start.</summary>
    Ready,

    Failed,
}

/// <summary>
/// Checks GitHub Releases at startup, every 24 hours and on request, and downloads a newer version; Velopack installs
/// it the next time the tray app starts (docs/PLAN.md, sections 6 and 8). Does nothing when RigShift was not installed
/// by Velopack (development builds). All members are used on the UI thread; events are raised there.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private readonly PeriodicTimer _timer = new(TimeSpan.FromHours(24));
    private readonly CancellationTokenSource _stop = new();
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private bool _checking;

    public UpdateService(ILogger log, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(log);
        _time = time;
        _log = log.ForContext<UpdateService>();
        IsInstalled = _manager.IsInstalled;
        State = IsInstalled ? UpdateState.NotChecked : UpdateState.NotInstalled;
        CurrentVersion = IsInstalled && _manager.CurrentVersion is { } installed
            ? installed.ToString()
            : Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "?";
    }

    /// <summary>Raised on the UI thread with the version that is ready to install.</summary>
    public event EventHandler<string>? UpdateReady;

    /// <summary>Raised on the UI thread whenever <see cref="State"/> or its details change.</summary>
    public event EventHandler? StateChanged;

    public bool IsInstalled { get; }

    public string CurrentVersion { get; }

    public UpdateState State { get; private set; }

    /// <summary>Version being downloaded or ready to install.</summary>
    public string? TargetVersion { get; private set; }

    public DateTimeOffset? LastChecked { get; private set; }

    public bool CanCheck => IsInstalled && !_checking;

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

        _checking = true;
        string? readyVersion = State == UpdateState.Ready ? TargetVersion : null;
        SetState(readyVersion is null ? UpdateState.Checking : UpdateState.Ready, readyVersion);
        try
        {
            UpdateInfo? update = await _manager.CheckForUpdatesAsync();
            LastChecked = _time.GetLocalNow();
            if (update is null)
            {
                _log.Information("No update available, installed version {Version}", CurrentVersion);
                SetState(UpdateState.UpToDate, null);
                return;
            }

            string version = update.TargetFullRelease.Version.ToString();
            if (version == readyVersion)
            {
                SetState(UpdateState.Ready, version);
                return;
            }

            _log.Information("Downloading update {Version}", version);
            SetState(UpdateState.Downloading, version);
            await _manager.DownloadUpdatesAsync(update, cancelToken: _stop.Token);
            _log.Information("Update {Version} downloaded, it is installed on the next start", version);
            SetState(UpdateState.Ready, version);
            UpdateReady?.Invoke(this, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Update check failed");
            SetState(readyVersion is null ? UpdateState.Failed : UpdateState.Ready, readyVersion);
        }
        finally
        {
            _checking = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SetState(UpdateState state, string? targetVersion)
    {
        State = state;
        TargetVersion = targetVersion;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
