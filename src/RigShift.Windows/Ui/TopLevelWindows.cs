using System.Diagnostics;
using System.Runtime.InteropServices;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>
/// What <see cref="WindowRescuer"/> and <see cref="WindowLayoutManager"/> share: which top-level windows are the user's,
/// the refusal to wait on a program that is not responding, and the workspace coordinates <c>WINDOWPLACEMENT</c> uses.
/// </summary>
internal static class TopLevelWindows
{
    public const int ErrorAccessDenied = 5;

    private const int ErrorTimeout = 1460;
    private const uint ResponseTimeoutMilliseconds = 250;

    /// <summary>Every top-level window, topmost first.</summary>
    public static List<HWND> All()
    {
        var windows = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, default);
        return windows;
    }

    /// <summary>Visible, not a tool window, not cloaked – a window the user can see and would miss.</summary>
    public static unsafe bool IsUserWindow(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd))
        {
            return false;
        }

        var exStyle = (WINDOW_EX_STYLE)(uint)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((exStyle & WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        // Cloaked: on another virtual desktop, a suspended UWP frame – visible to EnumWindows, not to the user.
        uint cloaked = 0;
        return !(PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0);
    }

    /// <summary>
    /// Hung as Windows sees it (no message processed for 5 s), or no answer to <c>WM_NULL</c> within 250 ms.
    /// <c>SetWindowPlacement</c> waits for the window's thread, so a hung program (shader compile, dashboards still
    /// loading) would stall the caller. A refused message (elevated window) is not "hung" – <c>SetWindowPlacement</c>
    /// reports that itself.
    /// </summary>
    public static bool IsHung(HWND hwnd)
    {
        if (PInvoke.IsHungAppWindow(hwnd))
        {
            return true;
        }

        LRESULT answered = PInvoke.SendMessageTimeout(hwnd, PInvoke.WM_NULL, default, default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK, ResponseTimeoutMilliseconds);
        return answered.Value == 0 && Marshal.GetLastPInvokeError() == ErrorTimeout;
    }

    /// <summary>The primary display's work area, or <c>null</c> when it cannot be read.</summary>
    public static unsafe Workspace? PrimaryWorkspace()
    {
        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(primary, &info))
        {
            return null;
        }

        return new Workspace(ToPixel(info.rcWork), info.rcWork.left - info.rcMonitor.left, info.rcWork.top - info.rcMonitor.top);
    }

    public static unsafe bool TryGetPlacement(HWND hwnd, out WINDOWPLACEMENT placement)
    {
        placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        return PInvoke.GetWindowPlacement(hwnd, ref placement);
    }

    /// <summary>
    /// Gives the window a new normal position (screen coordinates) and state without taking the focus – a maximized window
    /// has no non-activating variant. Returns 0, or the Win32 error (<see cref="ErrorAccessDenied"/>: the window belongs to
    /// an elevated program).
    /// </summary>
    public static int Place(HWND hwnd, WINDOWPLACEMENT placement, PixelRect bounds, WindowState state, Workspace workspace)
    {
        placement.rcNormalPosition = workspace.ToWorkspace(bounds);
        placement.showCmd = state switch
        {
            WindowState.Minimized => SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE,
            WindowState.Maximized => SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED,
            _ => SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE,
        };

        return PInvoke.SetWindowPlacement(hwnd, in placement) ? 0 : Marshal.GetLastPInvokeError();
    }

    public static WindowState StateOf(SHOW_WINDOW_CMD showCmd) => showCmd switch
    {
        SHOW_WINDOW_CMD.SW_SHOWMINIMIZED => WindowState.Minimized,
        SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED => WindowState.Maximized,
        _ => WindowState.Normal,
    };

    /// <summary>The name of the window's program, or <c>null</c> when it has ended or cannot be asked.</summary>
    public static string? ProcessName(HWND hwnd, out uint processId)
    {
        PInvoke.GetWindowThreadProcessId(hwnd, out processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static PixelRect ToPixel(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    public static RECT ToRect(PixelRect rect) => new() { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
}

/// <summary>
/// The primary work area in screen coordinates, and the offset of the workspace coordinates <c>WINDOWPLACEMENT</c> uses:
/// screen coordinates shifted by the work area's corner. Getting this wrong moves every window by the taskbar's height.
/// </summary>
internal readonly record struct Workspace(PixelRect WorkArea, int OffsetX, int OffsetY)
{
    public PixelRect ToScreen(RECT workspace) =>
        new(workspace.left + OffsetX, workspace.top + OffsetY, workspace.right + OffsetX, workspace.bottom + OffsetY);

    public RECT ToWorkspace(PixelRect screen) =>
        new() { left = screen.Left - OffsetX, top = screen.Top - OffsetY, right = screen.Right - OffsetX, bottom = screen.Bottom - OffsetY };
}
