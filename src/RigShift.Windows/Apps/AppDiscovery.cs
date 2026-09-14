using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Apps;

/// <summary>A program the app picker offers: a Start menu entry, a running app with a window, or both.</summary>
public sealed record DiscoveredApp(string Name, string Path, bool IsRunning);

/// <summary>
/// Programs for the app picker instead of a file dialog (finding HW-11): Start menu shortcuts of all users and the current
/// user that point to an <c>.exe</c>, plus running apps with a window. Read-only.
/// </summary>
public static class AppDiscovery
{
    public static IReadOnlyList<DiscoveredApp> Find(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        long started = Stopwatch.GetTimestamp();
        List<DiscoveredApp> startMenu = StartMenu(log);
        List<DiscoveredApp> running = Running(log);
        IReadOnlyList<DiscoveredApp> apps = Merge(startMenu, running);
        log.Information("App picker: {StartMenu} Start menu programs, {Running} running apps, {Total} in total after {Milliseconds:0} ms",
            startMenu.Count, running.Count, apps.Count, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return apps;
    }

    /// <summary>One entry per executable; running apps first, then by name. A running app keeps its Start menu name.</summary>
    public static IReadOnlyList<DiscoveredApp> Merge(IEnumerable<DiscoveredApp> startMenu, IEnumerable<DiscoveredApp> running)
    {
        ArgumentNullException.ThrowIfNull(startMenu);
        ArgumentNullException.ThrowIfNull(running);

        var byPath = new Dictionary<string, DiscoveredApp>(StringComparer.OrdinalIgnoreCase);
        foreach (DiscoveredApp app in startMenu.Where(a => !IsUninstaller(a)))
        {
            byPath.TryAdd(app.Path, app with { IsRunning = false });
        }

        foreach (DiscoveredApp app in running)
        {
            byPath[app.Path] = (byPath.TryGetValue(app.Path, out DiscoveredApp? known) ? known : app) with { IsRunning = true };
        }

        return [.. byPath.Values
            .OrderByDescending(a => a.IsRunning)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>Every word of the query appears in the name or the file name.</summary>
    public static bool Matches(DiscoveredApp app, string? query)
    {
        ArgumentNullException.ThrowIfNull(app);
        string fileName = System.IO.Path.GetFileName(app.Path);
        return (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(word => app.Name.Contains(word, StringComparison.CurrentCultureIgnoreCase)
                || fileName.Contains(word, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Uninstallers sit next to many programs in the Start menu; nobody starts them with a profile.</summary>
    public static bool IsUninstaller(DiscoveredApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        string file = System.IO.Path.GetFileNameWithoutExtension(app.Path);
        return file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            || app.Name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || app.Name.Contains("deinstall", StringComparison.OrdinalIgnoreCase);
    }

    private static List<DiscoveredApp> StartMenu(ILogger log)
    {
        var apps = new List<DiscoveredApp>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (Environment.SpecialFolder folder in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
        {
            string root = Environment.GetFolderPath(folder);
            if (root.Length == 0 || !Directory.Exists(root))
            {
                continue;
            }

            foreach (string shortcut in Directory.EnumerateFiles(root, "*.lnk", options))
            {
                if (ShortcutTarget(shortcut, log) is { } target
                    && target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && !IsInWindowsFolder(target)
                    && File.Exists(target))
                {
                    apps.Add(new DiscoveredApp(System.IO.Path.GetFileNameWithoutExtension(shortcut), target, IsRunning: false));
                }
            }
        }

        return apps;
    }

    private static List<DiscoveredApp> Running(ILogger log)
    {
        var apps = new List<DiscoveredApp>();
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id == Environment.ProcessId || process.MainWindowHandle == 0 || process.MainModule?.FileName is not { } path
                        || IsInWindowsFolder(path))
                    {
                        continue;
                    }

                    string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    apps.Add(new DiscoveredApp(string.IsNullOrWhiteSpace(description) ? process.ProcessName : description.Trim(), path, IsRunning: true));
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException or FileNotFoundException)
                {
                    // Elevated or already exited: its path cannot be read, so it cannot be offered.
                    log.Debug("Process {ProcessId} skipped for the app picker: {Reason}", process.Id, ex.Message);
                }
            }
        }

        return apps;
    }

    /// <summary>The executable a shortcut starts, with environment variables expanded; null if it cannot be read.</summary>
    private static string? ShortcutTarget(string shortcut, ILogger log)
    {
        var link = new ShellLink();
        try
        {
            ((IPersistFile)link).Load(shortcut, STGM.STGM_READ);
            Span<char> buffer = stackalloc char[1024];
            WIN32_FIND_DATAW data = default;
            ((IShellLinkW)link).GetPath(buffer, ref data, 0);
            int end = buffer.IndexOf('\0');
            string target = (end < 0 ? buffer : buffer[..end]).ToString();
            return target.Length == 0 ? null : target;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or ArgumentException)
        {
            log.Debug("Shortcut {Shortcut} could not be read: {Reason}", System.IO.Path.GetFileName(shortcut), ex.Message);
            return null;
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>Explorer, installer icons (advertised shortcuts) and system tools are no apps for a profile.</summary>
    private static bool IsInWindowsFolder(string path) =>
        path.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
