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
    // Only the shell can start an elevated program, and then without our environment.
    private const int ErrorElevationRequired = 740;

    private readonly IOpenXrRuntimes _runtimes;
    private readonly ILogger _log;

    public ShellGameStarter(IOpenXrRuntimes runtimes, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(runtimes);
        ArgumentNullException.ThrowIfNull(log);
        _runtimes = runtimes;
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
        ProcessStartInfo start = ShellStart(file, launch.Arguments);
        if (file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && RuntimeFor(launch) is { } runtime)
        {
            // The shell cannot hand over an environment, so this one start goes straight through CreateProcess.
            start.UseShellExecute = false;
            start.Environment["XR_RUNTIME_JSON"] = runtime;
            _log.Information("Starting {File} with OpenXR runtime {Runtime}", file, runtime);
        }

        // Not disposed here: the session waits on this very handle, see IRunningGame.
        Process? process;
        try
        {
            process = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired && !start.UseShellExecute)
        {
            _log.Warning("{File} asks for administrator rights; started through the shell with the OpenXR runtime Windows has set", file);
            process = Process.Start(ShellStart(file, launch.Arguments));
        }

        _log.Information("Started {File} (process {ProcessId})", file, process?.Id);
        return process is null ? null : new StartedProcess(process, _log);
    }

    private static ProcessStartInfo ShellStart(string file, string? arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
        };

        // Many games look for their files next to the executable instead of their own folder.
        if (Path.GetDirectoryName(file) is { Length: > 0 } directory)
        {
            start.WorkingDirectory = directory;
        }

        return start;
    }

    // Null with a warning when the chosen runtime is no longer registered with Windows.
    private string? RuntimeFor(GameLaunch launch)
    {
        if (launch.OpenXrRuntime is not { Length: > 0 } wanted)
        {
            return null;
        }

        if (_runtimes.Installed().FirstOrDefault(r => string.Equals(r.ManifestPath, wanted, StringComparison.OrdinalIgnoreCase)) is { } runtime)
        {
            return runtime.ManifestPath;
        }

        _log.Warning("OpenXR runtime {Runtime} is not registered with Windows (any more); the game starts with the one Windows has set", wanted);
        return null;
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
