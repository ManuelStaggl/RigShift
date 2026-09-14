using System.Diagnostics;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;

namespace RigShift.Windows.Apps;

/// <summary>
/// <see cref="IProcessList"/> over <see cref="Process.GetProcesses()"/>. Polling instead of WMI process events, which need
/// administrator rights (docs/PLAN.md, section 6).
/// </summary>
public sealed class ProcessList : IProcessList
{
    public IReadOnlySet<string> RunningProcessNames()
    {
        Process[] processes = Process.GetProcesses();
        var names = new HashSet<string>(ProcessNames.Comparer);
        foreach (Process process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch (InvalidOperationException)
            {
                // Exited in the meantime.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }
}
