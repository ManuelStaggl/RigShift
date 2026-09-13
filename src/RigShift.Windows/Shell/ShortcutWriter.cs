using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Shell;

/// <summary>Creates <c>.lnk</c> files via the shell's <c>IShellLinkW</c>.</summary>
public static class ShortcutWriter
{
    /// <param name="file">Full path of the <c>.lnk</c> file; an existing file is replaced.</param>
    public static void Create(string file, string target, string arguments, string description)
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
            shellLink.SetIconLocation(target, 0);
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
