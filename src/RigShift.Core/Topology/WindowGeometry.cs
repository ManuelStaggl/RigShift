namespace RigShift.Core.Topology;

/// <summary>A rectangle on the virtual desktop in physical pixels; right and bottom are exclusive, as in Win32.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public bool IsEmpty => Width <= 0 || Height <= 0;
}

/// <summary>Pure geometry for moving a lost window onto the primary display.</summary>
public static class WindowGeometry
{
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
