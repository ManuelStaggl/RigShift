using System.ComponentModel;
using System.Diagnostics;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Apps;

/// <summary>
/// <see cref="IAppLauncher"/> over <see cref="Process"/>. Programs are matched by file name, like Task Manager shows
/// them, because the full path of an elevated process cannot be read without elevation.
/// </summary>
public sealed class ProcessAppLauncher : IAppLauncher
{
    private readonly ILogger _log;

    public ProcessAppLauncher(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<ProcessAppLauncher>();
    }

    public bool IsRunning(string path)
    {
        Process[] processes = Find(path);
        DisposeAll(processes);
        return processes.Length > 0;
    }

    public void Start(string path, string? arguments)
    {
        string file = Expand(path);
        var start = new ProcessStartInfo
        {
            FileName = file,
            Arguments = arguments ?? string.Empty,
            UseShellExecute = true,
        };

        // Many sim tools look for their files next to the EXE instead of their own folder.
        if (Path.IsPathFullyQualified(file) && Path.GetDirectoryName(file) is { Length: > 0 } directory)
        {
            start.WorkingDirectory = directory;
        }

        using Process? process = Process.Start(start);
        _log.Debug("Started {File} (process {ProcessId})", file, process?.Id);
    }

    public async Task<bool> StopAsync(string path, TimeSpan grace, CancellationToken cancellationToken)
    {
        Process[] processes = Find(path);
        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    bool asked = process.CloseMainWindow();
                    _log.Debug("Asked process {ProcessId} ({Name}) to close: {Asked}", process.Id, process.ProcessName, asked);
                }
                catch (InvalidOperationException)
                {
                    // Exited in the meantime.
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(grace);
            try
            {
                await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(timeout.Token)));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _log.Information("{Name} did not close within {Grace} s, ending it", Path.GetFileName(Expand(path)), grace.TotalSeconds);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                _log.Debug(ex, "Waiting for {Name} failed", Path.GetFileName(Expand(path)));
            }

            bool ended = true;
            foreach (Process process in processes)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                        _log.Information("Ended process {ProcessId} ({Name})", process.Id, process.ProcessName);
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Typically access denied: the program runs elevated and RigShift does not.
                    _log.Warning(ex, "Could not end process {ProcessId}", process.Id);
                    ended = false;
                }
            }

            return ended;
        }
        finally
        {
            DisposeAll(processes);
        }
    }

    private static Process[] Find(string path) =>
        Process.GetProcessesByName(Path.GetFileNameWithoutExtension(Expand(path)));

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static void DisposeAll(Process[] processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }
}
