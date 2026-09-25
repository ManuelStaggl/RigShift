using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>One program from the Windows uninstall list, the fields a game needs.</summary>
public sealed record UninstallEntry(string DisplayName, string? Publisher, string? InstallLocation, string? DisplayIcon);

/// <summary>
/// Games that come with their own installer rather than Steam or Epic (v4 finding U-07), from the uninstall list:
/// iRacing – most of its drivers use its own installer, and it is the sim RigShift knows best –, games from the EA app
/// and a few standalone sims. They start through their executable; the EA app and iRacing's own updater step in by
/// themselves.
/// </summary>
public sealed class UninstallLibrary
{
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    /// <summary>Sims that ship their own installer; matched against the start of the display name.</summary>
    private static readonly string[] StandaloneSims = ["Live for Speed", "Richard Burns Rally", "Kart Racing Pro", "rFactor"];

    private readonly ILogger _log;
    private readonly Func<IEnumerable<UninstallEntry>> _entries;
    private readonly string _iRacingDefault;

    /// <param name="entries">The uninstall list; the registry when omitted.</param>
    /// <param name="iRacingFolder">Where iRacing installs by default; tests point it elsewhere.</param>
    public UninstallLibrary(ILogger log, Func<IEnumerable<UninstallEntry>>? entries = null, string? iRacingFolder = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<UninstallLibrary>();
        _entries = entries ?? ReadRegistry;
        _iRacingDefault = iRacingFolder ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "iRacing");
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        var games = new List<InstalledGame>();
        foreach (UninstallEntry entry in _entries())
        {
            if (ToGame(entry) is { } game)
            {
                games.Add(game);
            }
        }

        // iRacing's installer does not always leave an entry behind; its default folder is the second place to look.
        if (!games.Any(g => g.Origin == "iRacing") && IRacing(_iRacingDefault) is { } iRacing)
        {
            games.Add(iRacing);
        }

        _log.Information("Uninstall list: {Count} games (iRacing, EA app, standalone sims)", games.Count);
        return games;
    }

    /// <summary>The game an uninstall entry stands for, or <c>null</c> when it is none of ours or has no executable.</summary>
    public static InstalledGame? ToGame(UninstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        string name = entry.DisplayName.Replace("®", string.Empty, StringComparison.Ordinal).Replace("™", string.Empty, StringComparison.Ordinal).Trim();
        if (name.StartsWith("iRacing", StringComparison.OrdinalIgnoreCase)
            && (entry.Publisher?.Contains("iRacing", StringComparison.OrdinalIgnoreCase) ?? false))
        {
            return entry.InstallLocation is { Length: > 0 } iRacingFolder ? IRacing(iRacingFolder) : null;
        }

        string? origin = IsEaGame(name, entry.Publisher) ? "EA"
            : StandaloneSims.Any(sim => name.StartsWith(sim, StringComparison.OrdinalIgnoreCase)) ? string.Empty
            : null;
        if (origin is null || ExecutableOf(entry) is not { } executable)
        {
            return null;
        }

        return new InstalledGame(name, new GameLaunch
        {
            Kind = GameLaunchKind.Executable,
            Target = executable,
            InstallFolder = entry.InstallLocation is { Length: > 0 } folder && Directory.Exists(folder) ? folder : Path.GetDirectoryName(executable),
        }, origin.Length > 0 ? origin : null);
    }

    /// <summary>
    /// iRacing as its interface starts it: <c>ui\iRacingUI.exe</c>. Named "iRacing" so the sim template recognizes it –
    /// the interface stays open while the sim comes and goes.
    /// </summary>
    private static InstalledGame? IRacing(string folder)
    {
        string ui = Path.Combine(folder, "ui", "iRacingUI.exe");
        return File.Exists(ui)
            ? new InstalledGame("iRacing", new GameLaunch { Kind = GameLaunchKind.Executable, Target = ui, InstallFolder = folder }, "iRacing")
            : null;
    }

    /// <summary>The EA app writes this publisher for its games; the app itself and its predecessor are no games.</summary>
    private static bool IsEaGame(string name, string? publisher) =>
        string.Equals(publisher?.Trim(), "Electronic Arts", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("EA app", StringComparison.OrdinalIgnoreCase)
        && !name.StartsWith("Origin", StringComparison.OrdinalIgnoreCase);

    /// <summary>The program the entry's icon comes from – for a game that is the game – unless it is an uninstaller.</summary>
    public static string? ExecutableOf(UninstallEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DisplayIcon is not { Length: > 0 } icon)
        {
            return null;
        }

        // "C:\Games\F1 24\F1_24.exe",0 – quotes and an icon index around the path.
        string path = icon.Trim();
        int comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _))
        {
            path = path[..comma];
        }

        path = path.Trim().Trim('"');
        string file = Path.GetFileName(path);
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            && !file.StartsWith("unins", StringComparison.OrdinalIgnoreCase)
            && !file.Contains("Touchup", StringComparison.OrdinalIgnoreCase)
            && !file.Contains("Cleanup", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path)
            ? path
            : null;
    }

    private List<UninstallEntry> ReadRegistry()
    {
        var entries = new List<UninstallEntry>();
        foreach ((RegistryHive hive, RegistryView view) in new[]
        {
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
            (RegistryHive.CurrentUser, RegistryView.Default),
        })
        {
            try
            {
                using RegistryKey root = RegistryKey.OpenBaseKey(hive, view);
                using RegistryKey? uninstall = root.OpenSubKey(UninstallKey);
                if (uninstall is null)
                {
                    continue;
                }

                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using RegistryKey? key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is string displayName && key.GetValue("SystemComponent") is not 1)
                    {
                        entries.Add(new UninstallEntry(displayName, key.GetValue("Publisher") as string,
                            key.GetValue("InstallLocation") as string, key.GetValue("DisplayIcon") as string));
                    }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _log.Debug(ex, "Uninstall list of {Hive} ({View}) could not be read", hive, view);
            }
        }

        return entries;
    }
}
