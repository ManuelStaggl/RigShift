using System.ComponentModel;
using System.Diagnostics;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// <see cref="IGameProcesses"/> over <see cref="Process"/>. Only processes with a main window are listed: a game has
/// one even in exclusive fullscreen, while the store client's helpers, overlays and services do not, which keeps
/// them out of the learning before the install folder is even looked at.
/// </summary>
public sealed class SystemGameProcesses : IGameProcesses
{
    private readonly ILogger _log;

    public SystemGameProcesses(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<SystemGameProcesses>();
    }

    public IReadOnlyList<RunningProcess> List()
    {
        var result = new List<RunningProcess>();
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                if (process.MainWindowHandle != nint.Zero)
                {
                    result.Add(new RunningProcess(process.Id, process.ProcessName, ExecutablePath(process), process.StartTime));
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Ended while we looked at it, or a system process we may not read. One process must not hide the rest.
                _log.Verbose(ex, "Process {ProcessId} could not be read", SafeId(process));
            }
            finally
            {
                process.Dispose();
            }
        }

        return result;
    }

    public IReadOnlySet<string> FindRunning(IReadOnlySet<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            try
            {
                // The name comes with the process list; the main window costs a walk over every window on the desktop.
                string name = process.ProcessName;
                if (names.Contains(name) && !found.Contains(name) && process.MainWindowHandle != nint.Zero)
                {
                    found.Add(name);
                }
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                _log.Verbose(ex, "Process {ProcessId} could not be read", SafeId(process));
            }
            finally
            {
                process.Dispose();
            }
        }

        return found;
    }

    public async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return; // Already gone.
        }

        using (process)
        {
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or SystemException and not OperationCanceledException)
            {
                _log.Debug(ex, "Waiting for process {ProcessId} failed, treating it as ended", processId);
            }
        }
    }

    public bool IsRunning(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        Process[] processes = Process.GetProcessesByName(processName);
        foreach (Process process in processes)
        {
            process.Dispose();
        }

        return processes.Length > 0;
    }

    private static string? ExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Elevated or already exited: the caller treats an unknown path as "could be the game".
            return null;
        }
    }

    private static string SafeId(Process process)
    {
        try
        {
            return process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (InvalidOperationException)
        {
            return "?";
        }
    }
}
