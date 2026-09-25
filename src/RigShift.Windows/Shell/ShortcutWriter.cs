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

        (string target, string arguments, string iconFile, int iconIndex) = Read(oldFile);
        if (!Starts(target, executable) || !string.Equals(arguments, oldArguments, StringComparison.Ordinal))
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

    /// <summary>
    /// Deletes the shortcuts RigShift made in <paramref name="folder"/>: those that start <paramref name="executable"/>
    /// with a profile or game. Called before an uninstall, so the desktop keeps no dead symbols (v4 finding E-16). A
    /// shortcut that cannot be read or deleted is left alone; the others still go.
    /// </summary>
    /// <returns>The deleted files.</returns>
    public static IReadOnlyList<string> DeleteOwn(string folder, string executable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentException.ThrowIfNullOrWhiteSpace(executable);
        var deleted = new List<string>();
        if (!Directory.Exists(folder))
        {
            return deleted;
        }

        foreach (string file in Directory.EnumerateFiles(folder, "*.lnk"))
        {
            try
            {
                (string target, string arguments, _, _) = Read(file);
                if (Starts(target, executable)
                    && (arguments.StartsWith("apply ", StringComparison.Ordinal) || arguments.StartsWith("play ", StringComparison.Ordinal)))
                {
                    File.Delete(file);
                    deleted.Add(file);
                }
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
            {
                // Someone else's broken or locked shortcut is not ours to deal with.
            }
        }

        return deleted;
    }

    private static (string Target, string Arguments, string IconFile, int IconIndex) Read(string file)
    {
        var link = new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, STGM.STGM_READ);
            var shellLink = (IShellLinkW)link;
            Span<char> buffer = stackalloc char[1024];
            WIN32_FIND_DATAW data = default;
            shellLink.GetPath(buffer, ref data, 0);
            string target = Text(buffer);
            shellLink.GetArguments(buffer);
            string arguments = Text(buffer);
            shellLink.GetIconLocation(buffer, out int iconIndex);
            return (target, arguments, Text(buffer), iconIndex);
        }
        finally
        {
            Marshal.ReleaseComObject(link);
        }
    }

    private static bool Starts(string target, string executable) =>
        target.Length > 0 && string.Equals(Path.GetFullPath(target), Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase);

    private static string Text(ReadOnlySpan<char> buffer) => buffer.IndexOf('\0') is var end and >= 0 ? new string(buffer[..end]) : new string(buffer);

    /// <summary>A file name for <paramref name="name"/> with characters Windows does not allow replaced.</summary>
    public static string SafeFileName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
    }
}
