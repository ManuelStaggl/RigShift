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
}
