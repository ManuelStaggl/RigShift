using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>A rectangle on the virtual desktop in physical pixels; right and bottom are exclusive, as in Win32.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Pure geometry for reading, restoring and rescuing window positions.</summary>
public static class WindowGeometry
{
    private static readonly WindowState[] RestoreThenMaximize = [WindowState.Normal, WindowState.Maximized];

    /// <summary>
    /// Where an open window really is. A window snapped to a side or a corner reports the place it had before as its
    /// normal position; where it really is, only its <paramref name="currentRect"/> says. A maximized window keeps a
    /// normal position as well, and after a display change that can lie on another display than the one it fills –
    /// the one it fills (<paramref name="filledWorkAreaElsewhere"/>) is where it is.
    /// </summary>
    public static PixelRect ReportedBounds(
        WindowState state, PixelRect normalPosition, PixelRect? currentRect, PixelRect? filledWorkAreaElsewhere) =>
        state switch
        {
            WindowState.Normal when currentRect is { } current => current,
            WindowState.Maximized when filledWorkAreaElsewhere is { } filled => CenteredIn(normalPosition, filled),
            _ => normalPosition,
        };

    /// <summary>
    /// The placements to apply, in order, to put a window at a target. Empty when the target lies on no active display:
    /// the window would vanish there. A window that is maximized already stays on its display, because
    /// <c>SetWindowPlacement</c> only rewrites its normal position – it has to come down onto the other display first and
    /// is maximized there.
    /// </summary>
    public static IReadOnlyList<WindowState> PlacementSteps(
        WindowState target, bool targetOnActiveDisplay, bool maximizedNow, bool fillsAnotherDisplay)
    {
        if (!targetOnActiveDisplay)
        {
            return [];
        }

        return target == WindowState.Maximized && maximizedNow && fillsAnotherDisplay ? RestoreThenMaximize : [target];
    }

    /// <summary>The window's size, shrunk to fit the work area, centered in it.</summary>
    public static PixelRect CenteredIn(PixelRect window, PixelRect workArea)
    {
        int width = Math.Clamp(window.Width, 0, Math.Max(0, workArea.Width));
        int height = Math.Clamp(window.Height, 0, Math.Max(0, workArea.Height));
        int left = workArea.Left + ((workArea.Width - width) / 2);
        int top = workArea.Top + ((workArea.Height - height) / 2);
        return new PixelRect(left, top, left + width, top + height);
    }
}
