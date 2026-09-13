using Serilog;
using Velopack;
using Velopack.Sources;

namespace RigShift.App.Services;

/// <summary>
/// Checks GitHub Releases at startup and every 24 hours and downloads a newer version; Velopack installs it the next
/// time the tray app starts (docs/PLAN.md, section 8). Does nothing when RigShift was not installed by Velopack
/// (development builds).
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string RepositoryUrl = "https://github.com/ManuelStaggl/RigShift";

    private readonly UpdateManager _manager = new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
    private readonly PeriodicTimer _timer = new(TimeSpan.FromHours(24));
    private readonly CancellationTokenSource _stop = new();
    private readonly ILogger _log;
    private string? _downloadedVersion;

    public UpdateService(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<UpdateService>();
    }

    /// <summary>Raised on the UI thread with the version that is ready to install.</summary>
    public event EventHandler<string>? UpdateReady;

    public void Start()
    {
        if (!_manager.IsInstalled)
        {
            _log.Information("Not installed by Velopack, update checks are off");
            return;
        }

        _ = RunAsync();
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
        try
        {
            UpdateInfo? update = await _manager.CheckForUpdatesAsync();
            if (update is null)
            {
                _log.Information("No update available, installed version {Version}", _manager.CurrentVersion);
                return;
            }

            string version = update.TargetFullRelease.Version.ToString();
            if (version == _downloadedVersion)
            {
                return;
            }

            _log.Information("Downloading update {Version}", version);
            await _manager.DownloadUpdatesAsync(update, cancelToken: _stop.Token);
            _downloadedVersion = version;
            _log.Information("Update {Version} downloaded, it is installed on the next start", version);
            UpdateReady?.Invoke(this, version);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Update check failed");
        }
    }
}
