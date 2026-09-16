using RigShift.Core.Topology;

namespace RigShift.Core.Profiles;

/// <summary>
/// Where the helper windows belong: SimHub on the left screen, Crew Chief bottom right, the delta tool above the
/// wheel. Dragging them back into place after every switch is the part of the evening nobody wants, and no other
/// launcher does it.
///
/// The positions are plain virtual-desktop coordinates in physical pixels, and they are reliable **because RigShift
/// applies the display arrangement first**: a tool that saves its own position has no idea which screens will exist
/// when it comes back. That order is what makes this work at all.
/// </summary>
public sealed record WindowLayout
{
    /// <summary>The windows to put back, in the order they were captured.</summary>
    public IReadOnlyList<WindowPlacement> Windows { get; init; } = [];

    /// <summary>When the layout was captured, so the editor can say how old it is.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    public bool IsEmpty => Windows.Count == 0;

    /// <summary>Longest wait for a window to turn up after its program was started.</summary>
    public static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(20);

    /// <summary>How often to look for windows that are not there yet.</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
}

/// <summary>One window's place on the desktop.</summary>
public sealed record WindowPlacement
{
    /// <summary>Process name without extension, as Task Manager shows it – what a window is matched by.</summary>
    public required string ProcessName { get; init; }

    /// <summary>
    /// Window title at capture time, to tell several windows of one program apart. Titles change (a lap counter, a
    /// version number), so it only ever decides between candidates – it never rules the last one out.
    /// </summary>
    public string? Title { get; init; }

    /// <summary>Position and size when not minimized or maximized, in virtual-desktop pixels.</summary>
    public required PixelRect Bounds { get; init; }

    public WindowState State { get; init; }
}

public enum WindowState
{
    Normal,

    Minimized,

    Maximized,
}
