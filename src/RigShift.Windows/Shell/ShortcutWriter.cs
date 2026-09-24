using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Shell;

/// <summary>Creates and retargets <c>.lnk</c> files via the shell's <c>IShellLinkW</c>.</summary>
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

    /// <summary>
    /// Points a shortcut RigShift made at a renamed profile or game (v4 finding U-12): only a <c>.lnk</c> that starts
    /// <paramref name="executable"/> with exactly <paramref name="oldArguments"/> is touched – a game's own shortcut of the
    /// same name, from its installer, stays as it is. The icon is kept.
    /// </summary>
    /// <returns>True when the shortcut now carries the new name (and file name).</returns>
    public static bool Retarget(string oldFile, string newFile, string executable, string oldArguments, string newArguments, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(oldFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(newFile);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        if (!File.Exists(oldFile))
        {
            return false;
        }

        var link = new ShellLink();
        string target, arguments, iconFile;
        int iconIndex;
        try
        {
            ((IPersistFile)link).Load(oldFile, STGM.STGM_READ);
            var shellLink = (IShellLinkW)link;
            Span<char> buffer = stackalloc char[1024];
            WIN32_FIND_DATAW data = default;
            shellLink.GetPath(buffer, ref data, 0);
            target = Text(buffer);
            shellLink.GetArguments(buffer);
            arguments = Text(buffer);
            shellLink.GetIconLocation(buffer, out iconIndex);
            iconFile = Text(buffer);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }

        if (target.Length == 0
            || !string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase)
            || !string.Equals(arguments, oldArguments, StringComparison.Ordinal))
        {
            return false;
        }

        Create(newFile, executable, newArguments, description, iconFile.Length > 0 ? iconFile : null, iconIndex);
        if (!string.Equals(Path.GetFullPath(oldFile), Path.GetFullPath(newFile), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(oldFile);
        }

        return true;
    }

    private static string Text(ReadOnlySpan<char> buffer) => buffer.IndexOf('\0') is var end and >= 0 ? new string(buffer[..end]) : new string(buffer);

    /// <summary>A file name for <paramref name="name"/> with characters Windows does not allow replaced.</summary>
    public static string SafeFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
