using System.Diagnostics;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// <see cref="IGameStarter"/> over the shell. A game that came from a store is started through the store's URI, not
/// through its executable: overlay, anti-cheat and DRM expect the client to be in the chain, and some executables
/// restart themselves through the client or refuse outright. The executable from the manifest is there to recognise
/// the game, not to start it.
/// </summary>
public sealed class ShellGameStarter : IGameStarter
{
    private readonly ILogger _log;

    public ShellGameStarter(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<ShellGameStarter>();
    }

    public IRunningGame? Start(GameLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);
        if (!launch.HasValidTarget)
        {
            // The id ends up inside a URI that the store client acts on; anything but an id does not belong there.
            throw new InvalidOperationException($"'{launch.Target}' is not a valid {launch.Kind} id.");
        }

        if (launch.Uri is { Length: > 0 } uri)
        {
            using Process? client = Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
            _log.Information("Started {Uri} (through the store client, process {ProcessId})", uri, client?.Id);
            return null;
        }

        string file = LaunchPath.ForStart(launch.Target);
        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = launch.Arguments ?? string.Empty,
            UseShellExecute = true,
        };

        // Many games look for their files next to the executable instead of their own folder.
        if (Path.GetDirectoryName(file) is { Length: > 0 } directory)
        {
            start.WorkingDirectory = directory;
        }

        // Not disposed here: the session waits on this very handle, see IRunningGame.
        Process? process = Process.Start(start);
        _log.Information("Started {File} (process {ProcessId})", file, process?.Id);
        return process is null ? null : new StartedProcess(process, _log);
    }

    private sealed class StartedProcess(Process process, ILogger log) : IRunningGame
    {
        public int Id { get; } = process.Id;

        public async Task WaitForExitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // No handle to wait on, e.g. the shell started it elevated. The caller falls back to the name.
                log.Debug(ex, "Waiting for process {ProcessId} failed, treating it as ended", Id);
            }
        }

        public void Dispose() => process.Dispose();
    }
}
