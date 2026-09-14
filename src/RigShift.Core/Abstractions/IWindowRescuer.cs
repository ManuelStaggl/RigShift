namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for windows that end up on a display that is off after a switch (Discord, Steam, SimHub remember their
/// last position). Always on, no setting (docs/PLAN.md, section 6, "Neu für 1.3", item 1).
/// </summary>
public interface IWindowRescuer
{
    /// <summary>
    /// Moves every visible top-level window that lies on no active display to the primary display's work area, keeping
    /// its minimized or maximized state. Windows that cannot be moved (elevated, access denied) are skipped.
    /// </summary>
    /// <returns>How many windows were moved.</returns>
    int RescueOffscreenWindows();
}
