using System.Diagnostics;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// Finds the executable of a configured game on disk. Not used to start it – a store game is always started through
/// its client – but to take an icon from: a desktop shortcut for a game should show the game.
/// </summary>
public static class GameExecutable
{
    /// <summary>
    /// How long the search through the install folder may take. An install folder can hold tens of thousands of files,
    /// and this runs while the user waits for a shortcut; without an icon the shortcut is still fine.
    /// </summary>
    private static readonly TimeSpan SearchBudget = TimeSpan.FromSeconds(3);

    /// <summary>The game's executable, or <c>null</c> when it cannot be found.</summary>
    public static string? Find(GameLaunch launch, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(log);

        if (launch.Kind == GameLaunchKind.Executable)
        {
            string path = Environment.ExpandEnvironmentVariables(launch.Target.Trim().Trim('"'));
            return File.Exists(path) ? path : null;
        }

        // Steam and Epic name the game by an id, so the executable is only reachable through the install folder – and
        // only once the process name is known, which Epic's manifest gives us and Steam's first start learns.
        if (launch.InstallFolder is not { Length: > 0 } folder || launch.ProcessName is not { Length: > 0 } process)
        {
            return null;
        }

        string file = process + ".exe";
        try
        {
            string top = Path.Combine(folder, file);
            if (File.Exists(top))
            {
                return top;
            }

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
            };

            // Every .exe rather than the one name, so the elapsed time can be checked between two files: with a name
            // filter the enumeration only comes back on a hit, and a folder without one would run to its end.
            long started = Stopwatch.GetTimestamp();
            foreach (string found in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                if (string.Equals(Path.GetFileName(found), file, StringComparison.OrdinalIgnoreCase))
                {
                    return found;
                }

                if (Stopwatch.GetElapsedTime(started) > SearchBudget)
                {
                    log.Information("Executable {File} not found under {Folder} within {Budget}", file, folder, SearchBudget);
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log.Warning(ex, "Executable {File} could not be looked for under {Folder}", file, folder);
        }

        return null;
    }

    /// <summary>Programs that come along with a game but are not it, by a part of their name or of a folder they sit in.</summary>
    private static readonly string[] Helpers =
    [
        "unins", "setup", "install", "crash", "report", "redist", "vc_redist", "vcredist", "dxsetup", "dotnet", "UnityCrashHandler",
        "EasyAntiCheat", "BEService", "BattlEye", "launcher", "_CommonRedist", "DirectX", "Support",
    ];

    /// <summary>
    /// The process name a store game most likely runs as before its first start: the largest program under its install
    /// folder that is no installer, crash reporter, anti-cheat or launcher. An Unreal game's small starter in the top
    /// folder loses to its <c>…-Win64-Shipping.exe</c>, which is the process that runs. A guess: the first start through
    /// RigShift replaces it.
    /// </summary>
    public static string? GuessProcessName(string installFolder, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(installFolder);
        ArgumentNullException.ThrowIfNull(log);
        if (!Directory.Exists(installFolder))
        {
            return null;
        }

        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        long started = Stopwatch.GetTimestamp();
        (string? Path, long Length) best = (null, -1);
        try
        {
            foreach (string file in Directory.EnumerateFiles(installFolder, "*.exe", options))
            {
                if (Stopwatch.GetElapsedTime(started) > SearchBudget)
                {
                    log.Information("Process name of {Folder} guessed from what was found within {Budget}", installFolder, SearchBudget);
                    break;
                }

                string[] parts = Path.GetRelativePath(installFolder, file).Split(Path.DirectorySeparatorChar);
                if (parts.Any(part => Helpers.Any(h => part.Contains(h, StringComparison.OrdinalIgnoreCase))))
                {
                    continue;
                }

                long length = new FileInfo(file).Length;
                if (length > best.Length)
                {
                    best = (file, length);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "Programs under {Folder} could not be listed for a process name", installFolder);
        }

        return best.Path is null ? null : Path.GetFileNameWithoutExtension(best.Path);
    }
}
