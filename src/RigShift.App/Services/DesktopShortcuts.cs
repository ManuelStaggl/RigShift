using System.IO;
using System.Runtime.InteropServices;
using RigShift.App.Localization;
using RigShift.Core.Cli;
using RigShift.Windows.Shell;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The desktop shortcuts "Desktop shortcut" makes: where they go, what they run, and how they follow a rename (v4
/// finding U-12). A profile's is "RigShift – Name", a game's carries the game's own name.
/// </summary>
public static class DesktopShortcuts
{
    private static string Desktop => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

    public static string ProfileTitle(string name) => "RigShift – " + ShortcutWriter.SafeFileName(name);

    public static string ProfileFile(string name) => Path.Combine(Desktop, ProfileTitle(name) + ".lnk");

    public static string ProfileArguments(string name) => "apply " + CommandLineArguments.Quote(name);

    public static string GameTitle(string name) => ShortcutWriter.SafeFileName(name);

    public static string GameFile(string name) => Path.Combine(Desktop, GameTitle(name) + ".lnk");

    public static string GameArguments(string name) => "play " + CommandLineArguments.Quote(name);

    /// <returns>True when a shortcut for <paramref name="oldName"/> was found and now switches to <paramref name="newName"/>.</returns>
    public static bool FollowProfileRename(string oldName, string newName, ILogger log) =>
        Follow(ProfileFile(oldName), ProfileFile(newName), ProfileArguments(oldName), ProfileArguments(newName), Loc.Format("Shortcut_Description", newName), log);

    /// <returns>True when a shortcut for <paramref name="oldName"/> was found and now starts <paramref name="newName"/>.</returns>
    public static bool FollowGameRename(string oldName, string newName, ILogger log) =>
        Follow(GameFile(oldName), GameFile(newName), GameArguments(oldName), GameArguments(newName), Loc.Format("Shortcut_GameDescription", newName), log);

    private static bool Follow(string oldFile, string newFile, string oldArguments, string newArguments, string description, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (Environment.ProcessPath is not { } executable || !File.Exists(oldFile))
        {
            return false;
        }

        try
        {
            bool moved = ShortcutWriter.Retarget(oldFile, newFile, executable, oldArguments, newArguments, description);
            if (moved)
            {
                log.Information("Desktop shortcut {Old} follows the rename to {New}", oldFile, newFile);
            }

            return moved;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            log.Warning(ex, "Desktop shortcut {File} could not follow the rename", oldFile);
            return false;
        }
    }
}
