using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>
/// <see cref="IWindowLayout"/> over <c>EnumWindows</c> and <c>SetWindowPlacement</c>, on the same ground as
/// <see cref="WindowRescuer"/> (<see cref="TopLevelWindows"/>). Only windows with a title are offered: a window without
/// one cannot be told apart in the list.
/// </summary>
public sealed class WindowLayoutManager : IWindowLayout
{
    private readonly ILogger _log;

    public WindowLayoutManager(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<WindowLayoutManager>();
    }

    public unsafe IReadOnlyList<OpenWindow> Open()
    {
        List<HWND> handles = TopLevelWindows.All();
        if (PrimaryWorkspace() is not { } workspace)
        {
            return [];
        }

        var windows = new List<OpenWindow>();
        foreach (HWND hwnd in handles)
        {
            if (PInvoke.GetWindowTextLength(hwnd) == 0
                || !TopLevelWindows.IsUserWindow(hwnd)
                || !TopLevelWindows.TryGetPlacement(hwnd, out WINDOWPLACEMENT placement))
            {
                continue;
            }

            WindowState state = TopLevelWindows.StateOf(placement.showCmd);
            PixelRect bounds = workspace.ToScreen(placement.rcNormalPosition);

            // A window snapped to a side or a corner reports the place it had before as its normal position; where it
            // really is, only its rectangle says. A maximized window keeps a normal position as well, and after a display
            // change that can lie on another display than the one the window fills – the one it fills is where it is.
            if (state == WindowState.Normal && PInvoke.GetWindowRect(hwnd, out RECT current))
            {
                bounds = TopLevelWindows.ToPixel(current);
            }
            else if (state == WindowState.Maximized && FilledWorkAreaElsewhere(hwnd, bounds) is { } filled)
            {
                bounds = WindowGeometry.CenteredIn(bounds, filled);
            }

            if (bounds.IsEmpty)
            {
                continue;
            }

            windows.Add(new OpenWindow((nint)hwnd.Value, ProcessName(hwnd), Title(hwnd), bounds, state));
        }

        return windows;
    }

    public bool Place(nint windowHandle, PixelRect bounds, WindowState state)
    {
        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return false;
        }

        // A place on a display that is off now (an optional one is missing, the profile was rearranged since): the
        // window would vanish there.
        if (PInvoke.MonitorFromRect(TopLevelWindows.ToRect(bounds), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull)
        {
            _log.Information("Window of {Process} not placed: {Bounds} lies on no active display", ProcessName(hwnd), bounds);
            return false;
        }

        if (TopLevelWindows.IsHung(hwnd))
        {
            _log.Warning("Window of {Process} not placed: the program is not responding", ProcessName(hwnd));
            return false;
        }

        if (!TopLevelWindows.TryGetPlacement(hwnd, out WINDOWPLACEMENT placement) || PrimaryWorkspace() is not { } workspace)
        {
            return false;
        }

        // A window that is maximized already stays on its display: SetWindowPlacement only rewrites its normal position.
        // It has to come down onto the other display first and is maximized there.
        int error = 0;
        if (state == WindowState.Maximized
            && placement.showCmd == SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED
            && FilledWorkAreaElsewhere(hwnd, bounds) is not null)
        {
            _log.Information("Window of {Process} is maximized on another display, restoring it before maximizing it again", ProcessName(hwnd));
            error = TopLevelWindows.Place(hwnd, placement, bounds, WindowState.Normal, workspace);
        }

        if (error == 0)
        {
            error = TopLevelWindows.Place(hwnd, placement, bounds, state, workspace);
        }

        if (error == 0)
        {
            _log.Information("Placed the window of {Process} at {Bounds} ({State})", ProcessName(hwnd), bounds, state);
            return true;
        }

        if (error == TopLevelWindows.ErrorAccessDenied)
        {
            _log.Warning("Window of {Process} could not be placed: access denied – the program runs elevated and RigShift does not",
                ProcessName(hwnd));
        }
        else
        {
            _log.Warning("Window of {Process} could not be placed (error {Error})", ProcessName(hwnd), error);
        }

        return false;
    }

    /// <summary>
    /// The work area of the display the window fills, when <paramref name="place"/> lies on another one; <c>null</c>
    /// when both are the same display.
    /// </summary>
    private static unsafe PixelRect? FilledWorkAreaElsewhere(HWND hwnd, PixelRect place)
    {
        HMONITOR filled = PInvoke.MonitorFromWindow(hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL);
        if (filled.IsNull || filled == PInvoke.MonitorFromRect(TopLevelWindows.ToRect(place), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST))
        {
            return null;
        }

        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        return PInvoke.GetMonitorInfo(filled, &info) ? TopLevelWindows.ToPixel(info.rcWork) : null;
    }

    private Workspace? PrimaryWorkspace()
    {
        Workspace? workspace = TopLevelWindows.PrimaryWorkspace();
        if (workspace is null)
        {
            _log.Warning("Primary monitor could not be read, window positions are unusable");
        }

        return workspace;
    }

    private static string Title(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetWindowText(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    private static string ProcessName(HWND hwnd) => TopLevelWindows.ProcessName(hwnd, out _) ?? string.Empty;
}
