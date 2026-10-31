using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>One game of GOG Galaxy, as <c>HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt;</c> holds it.</summary>
/// <param name="DependsOn">The game a DLC belongs to; empty for a game.</param>
public sealed record GogEntry(string? GameName, string? Exe, string? Path, string? DependsOn);

/// <summary>One game of Ubisoft Connect: its install folder and the uninstall entry <c>Uplay Install &lt;id&gt;</c>.</summary>
public sealed record UbisoftEntry(string? InstallDir, string? DisplayName, string? DisplayIcon);

/// <summary>
/// Games of GOG Galaxy. They are DRM-free and start through their executable, which also hands us the game's own
/// process; Galaxy is not needed to play.
/// </summary>
public sealed class GogLibrary
{
    private const string KeyPath = @"SOFTWARE\GOG.com\Games";

    private readonly ILogger _log;
    private readonly Func<IEnumerable<GogEntry>> _entries;

    /// <param name="entries">The games; the registry when omitted.</param>
    public GogLibrary(ILogger log, Func<IEnumerable<GogEntry>>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<GogLibrary>();
        _entries = entries ?? ReadRegistry;
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        List<InstalledGame> games = [.. _entries().Select(ToGame).OfType<InstalledGame>()];
        _log.Information("GOG: {Count} games", games.Count);
        return games;
    }

    /// <summary>The game of an entry, or <c>null</c> for a DLC or a game whose executable is gone.</summary>
    public static InstalledGame? ToGame(GogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!string.IsNullOrWhiteSpace(entry.DependsOn) || entry.GameName is not { Length: > 0 } name
            || entry.Exe is not { Length: > 0 } exe || !File.Exists(exe))
        {
            return null;
        }

        return new InstalledGame(name.Trim(), new GameLaunch
        {
            Kind = GameLaunchKind.Executable,
            Target = exe,
            InstallFolder = entry.Path is { Length: > 0 } folder && Directory.Exists(folder) ? folder : System.IO.Path.GetDirectoryName(exe),
        }, "GOG");
    }

    private List<GogEntry> ReadRegistry()
    {
        var entries = new List<GogEntry>();
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using RegistryKey? games = root.OpenSubKey(KeyPath);
            foreach (string id in games?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? key = games!.OpenSubKey(id);
                if (key is not null)
                {
                    entries.Add(new GogEntry(key.GetValue("gameName") as string, key.GetValue("exe") as string,
                        key.GetValue("path") as string, key.GetValue("dependsOn") as string));
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "GOG games could not be read");
        }

        return entries;
    }
}

/// <summary>
/// Games of Ubisoft Connect. They start through their executable: a Ubisoft game started that way brings Ubisoft
/// Connect up by itself, and RigShift still sees the game's process.
/// </summary>
public sealed class UbisoftLibrary
{
    private const string KeyPath = @"SOFTWARE\Ubisoft\Launcher\Installs";

    private readonly ILogger _log;
    private readonly Func<IEnumerable<UbisoftEntry>> _entries;

    /// <param name="entries">The games; the registry when omitted.</param>
    public UbisoftLibrary(ILogger log, Func<IEnumerable<UbisoftEntry>>? entries = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<UbisoftLibrary>();
        _entries = entries ?? ReadRegistry;
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        List<InstalledGame> games = [.. _entries().Select(ToGame).OfType<InstalledGame>()];
        _log.Information("Ubisoft: {Count} games", games.Count);
        return games;
    }

    /// <summary>The game of an entry, or <c>null</c> when its uninstall entry names no executable that exists.</summary>
    public static InstalledGame? ToGame(UbisoftEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.DisplayName is not { Length: > 0 } name
            || UninstallLibrary.ExecutableOf(new UninstallEntry(name, null, entry.InstallDir, entry.DisplayIcon)) is not { } exe)
        {
            return null;
        }

        // Ubisoft writes the folder with forward slashes and a trailing one.
        string? folder = entry.InstallDir?.Replace('/', '\\').TrimEnd('\\');
        return new InstalledGame(name.Trim(), new GameLaunch
        {
            Kind = GameLaunchKind.Executable,
            Target = exe,
            InstallFolder = folder is { Length: > 0 } && Directory.Exists(folder) ? folder : Path.GetDirectoryName(exe),
        }, "Ubisoft");
    }

    private List<UbisoftEntry> ReadRegistry()
    {
        var entries = new List<UbisoftEntry>();
        try
        {
            using RegistryKey root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using RegistryKey? installs = root.OpenSubKey(KeyPath);
            using RegistryKey? uninstall = root.OpenSubKey(UninstallRegistry.KeyPath);
            foreach (string id in installs?.GetSubKeyNames() ?? [])
            {
                using RegistryKey? install = installs!.OpenSubKey(id);
                using RegistryKey? program = uninstall?.OpenSubKey("Uplay Install " + id);
                entries.Add(new UbisoftEntry(install?.GetValue("InstallDir") as string,
                    program?.GetValue("DisplayName") as string, program?.GetValue("DisplayIcon") as string));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "Ubisoft games could not be read");
        }

        return entries;
    }
}
