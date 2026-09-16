using System.ComponentModel;
using System.Diagnostics;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Apps;

/// <summary>
/// <see cref="IAppLauncher"/> over <see cref="Process"/>. Programs are matched by file name, like Task Manager shows
/// them; where the executable path of a running process can be read, it must match a configured full path as well.
/// The path of an elevated process cannot be read without elevation, so for those the name alone decides.
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
                        // With its children: sim tools run background agents (telemetry upload, livery sync) that
                        // outlive the window and would keep the device or the port busy for the next session.
                        process.Kill(entireProcessTree: true);
                        _log.Information("Ended process {ProcessId} ({Name}) and its children", process.Id, process.ProcessName);
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

    /// <summary>
    /// Processes by file name; with a full path configured, those whose executable path is readable and different are
    /// left out, so stopping "C:\SimHub\SimHubWPF.exe" does not end a same-named program elsewhere (analysis finding G-03).
    /// </summary>
    private Process[] Find(string path)
    {
        string file = Expand(path);
        Process[] byName = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(file));
        if (!Path.IsPathFullyQualified(file))
        {
            return byName;
        }

        var matching = new List<Process>(byName.Length);
        foreach (Process process in byName)
        {
            string? executable = ExecutablePath(process);
            if (IsSameExecutable(file, executable))
            {
                matching.Add(process);
            }
            else
            {
                _log.Debug("Process {ProcessId} ({Name}) runs from {Executable}, not {File}: left alone", process.Id, process.ProcessName, executable, file);
                process.Dispose();
            }
        }

        return [.. matching];
    }

    /// <summary>True when the paths name the same file, or the running path is unknown (e.g. an elevated process).</summary>
    public static bool IsSameExecutable(string configuredPath, string? runningPath)
    {
        ArgumentNullException.ThrowIfNull(configuredPath);
        if (string.IsNullOrEmpty(runningPath))
        {
            return true;
        }

        try
        {
            return string.Equals(Path.GetFullPath(configuredPath), Path.GetFullPath(runningPath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    private static string? ExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Elevated or already exited: cannot tell, so the name decides as before.
            return null;
        }
    }

    private static string Expand(string path) => Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));

    private static void DisposeAll(Process[] processes)
    {
        foreach (Process process in processes)
        {
            process.Dispose();
        }
    }
}
