namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for keeping the mouse on one display.</summary>
public interface ICursorConfinement
{
    /// <summary>
    /// While the returned handle lives: whenever a window of <paramref name="processName"/> is in the foreground, the cursor
    /// cannot leave the display that window is on; once another window comes to the front it is free again. Disposing
    /// frees it for good.
    /// </summary>
    IDisposable KeepOnWindowOf(string processName);
}
