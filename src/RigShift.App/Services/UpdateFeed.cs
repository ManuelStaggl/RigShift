using Velopack;
using Velopack.Sources;

namespace RigShift.App.Services;

/// <summary>Where updates come from and how one is installed – Velopack in the app, a script in tests.</summary>
public interface IUpdateFeed
{
    /// <summary>Installed by the setup; a development build is not and never updates.</summary>
    bool IsInstalled { get; }

    /// <summary>The installed version, or <c>null</c> when not installed.</summary>
    string? InstalledVersion { get; }

    /// <summary>The newer release, or <c>null</c> when this one is the newest. Throws when the feed is unreachable.</summary>
    Task<UpdateInfo?> CheckAsync();

    Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, installs and starts RigShift again.</summary>
    void ApplyAfterExit(VelopackAsset release);
}

/// <summary>GitHub Releases through Velopack.</summary>
public sealed class VelopackUpdateFeed(string repositoryUrl) : IUpdateFeed
{
    private readonly UpdateManager _manager = new(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));

    public bool IsInstalled => _manager.IsInstalled;

    public string? InstalledVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    public Task<UpdateInfo?> CheckAsync() => _manager.CheckForUpdatesAsync();

    public Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken) =>
        _manager.DownloadUpdatesAsync(update, cancelToken: cancellationToken);

    public void ApplyAfterExit(VelopackAsset release) => _manager.WaitExitThenApplyUpdates(release, silent: true, restart: true);
}
