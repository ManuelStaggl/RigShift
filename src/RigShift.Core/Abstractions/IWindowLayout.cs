using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for reading and restoring where helper windows sit.</summary>
public interface IWindowLayout
{
    /// <summary>
    /// Every visible top-level window right now, with the program it belongs to and where it sits. The caller picks
    /// which ones to keep – capturing everything and filtering afterwards is the only way the user can be shown a
    /// list to tick.
    /// </summary>
    IReadOnlyList<OpenWindow> Open();

    /// <summary>
    /// Puts one window back. Returns false when it could not be moved – an elevated program, or one that is not
    /// responding.
    /// </summary>
    bool Place(nint windowHandle, PixelRect bounds, WindowState state);
}

/// <summary>A window that is open right now.</summary>
/// <param name="Handle">Its Win32 handle; only valid for as long as the window lives.</param>
/// <param name="ProcessName">Process name without extension.</param>
/// <param name="Title">Window title, which may be empty.</param>
/// <param name="Bounds">Position and size when not minimized or maximized, in virtual-desktop pixels.</param>
/// <param name="State">Whether it is minimized, maximized or neither.</param>
public sealed record OpenWindow(nint Handle, string ProcessName, string Title, PixelRect Bounds, WindowState State);
