using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Shell;

/// <summary>Creates <c>.lnk</c> files via the shell's <c>IShellLinkW</c>.</summary>
public static class ShortcutWriter
{
    /// <param name="file">Full path of the <c>.lnk</c> file; an existing file is replaced.</param>
    /// <param name="iconFile">
    /// File to take the icon from, <c>null</c> for <paramref name="target"/>'s own. A game shortcut points here at the
    /// game's executable, so the desktop shows the game rather than RigShift.
    /// </param>
    /// <param name="iconIndex">Index of the icon inside <paramref name="iconFile"/>; the first one is what a game has.</param>
    public static void Create(string file, string target, string arguments, string description, string? iconFile = null, int iconIndex = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(description);

        var link = new ShellLink();
        try
        {
            var shellLink = (IShellLinkW)link;
            shellLink.SetPath(target);
            shellLink.SetArguments(arguments);
            shellLink.SetDescription(description);
            shellLink.SetIconLocation(iconFile is { Length: > 0 } ? iconFile : target, iconIndex);
            shellLink.SetWorkingDirectory(Path.GetDirectoryName(target) ?? string.Empty);
            ((IPersistFile)link).Save(file, fRemember: true);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    /// <summary>A file name for <paramref name="name"/> with characters Windows does not allow replaced.</summary>
    public static string SafeFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
