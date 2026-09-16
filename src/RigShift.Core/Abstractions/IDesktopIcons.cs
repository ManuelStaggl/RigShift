using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for reading and restoring the positions of the desktop symbols.</summary>
public interface IDesktopIcons
{
    /// <summary>
    /// Where every symbol on the desktop sits right now, or <c>null</c> when the desktop view is not reachable – there
    /// is no desktop in a service or an SSH session, and Explorer may be restarting.
    /// </summary>
    DesktopIconLayout? Capture();

    /// <summary>
    /// Puts the captured symbols back. Symbols that are gone are skipped, symbols that were added since keep their
    /// place. Never throws: a layout that cannot be restored is worth a log line, not a failed switch.
    /// </summary>
    DesktopIconResult Restore(DesktopIconLayout layout);
}
