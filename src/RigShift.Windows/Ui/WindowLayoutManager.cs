using System.Diagnostics;
using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>
/// <see cref="IWindowLayout"/> over <c>EnumWindows</c> and <c>SetWindowPlacement</c>, built like
/// <see cref="WindowRescuer"/> – same candidate test, same handling of the workspace coordinates
/// <c>WINDOWPLACEMENT</c> uses, same refusal to wait on a program that is not responding.
/// </summary>
public sealed class WindowLayoutManager : IWindowLayout
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorTimeout = 1460;
    private const uint ResponseTimeoutMilliseconds = 250;

    private readonly ILogger _log;

    public WindowLayoutManager(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<WindowLayoutManager>();
    }

    public unsafe IReadOnlyList<OpenWindow> Open()
    {
        var handles = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            handles.Add(hwnd);
            return true;
        }, default);

        if (!WorkspaceOffset(out int offsetX, out int offsetY))
        {
            return [];
        }

        var windows = new List<OpenWindow>();
        foreach (HWND hwnd in handles)
        {
            if (!IsCandidate(hwnd))
            {
                continue;
            }

            var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!PInvoke.GetWindowPlacement(hwnd, ref placement))
            {
                continue;
            }

            RECT normal = placement.rcNormalPosition;
            var bounds = new PixelRect(
                normal.left + offsetX, normal.top + offsetY, normal.right + offsetX, normal.bottom + offsetY);
            if (bounds.IsEmpty)
            {
                continue;
            }

            windows.Add(new OpenWindow((nint)hwnd.Value, ProcessName(hwnd), Title(hwnd), bounds, ToState(placement.showCmd)));
        }

        return windows;
    }

    public unsafe bool Place(nint windowHandle, PixelRect bounds, WindowState state)
    {
        var hwnd = new HWND(windowHandle);
        if (!PInvoke.IsWindow(hwnd))
        {
            return false;
        }

        // SetWindowPlacement waits for the window's thread; a program still loading its dashboards would stall us.
        if (IsHung(hwnd))
        {
            _log.Warning("Window of {Process} not placed: the program is not responding", ProcessName(hwnd));
            return false;
        }

        var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
        if (!PInvoke.GetWindowPlacement(hwnd, ref placement) || !WorkspaceOffset(out int offsetX, out int offsetY))
        {
            return false;
        }

        placement.rcNormalPosition = new RECT
        {
            left = bounds.Left - offsetX,
            top = bounds.Top - offsetY,
            right = bounds.Right - offsetX,
            bottom = bounds.Bottom - offsetY,
        };

        // Placing a window must not steal the focus; a maximized window has no non-activating variant.
        placement.showCmd = state switch
        {
            WindowState.Minimized => SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE,
            WindowState.Maximized => SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED,
            _ => SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE,
        };

        if (PInvoke.SetWindowPlacement(hwnd, in placement))
        {
            _log.Information("Placed the window of {Process} at {Bounds} ({State})", ProcessName(hwnd), bounds, state);
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (error == ErrorAccessDenied)
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
    /// <c>WINDOWPLACEMENT</c> is in workspace coordinates: screen coordinates shifted by the primary work area's
    /// offset. Getting this wrong moves every window by the height of the taskbar.
    /// </summary>
    private unsafe bool WorkspaceOffset(out int offsetX, out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;
        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(primary, &info))
        {
            _log.Warning("Primary monitor could not be read, window positions are unusable");
            return false;
        }

        offsetX = info.rcWork.left - info.rcMonitor.left;
        offsetY = info.rcWork.top - info.rcMonitor.top;
        return true;
    }

    private static unsafe bool IsCandidate(HWND hwnd)
    {
        if (!PInvoke.IsWindowVisible(hwnd) || PInvoke.GetWindowTextLength(hwnd) == 0)
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

    private static bool IsHung(HWND hwnd)
    {
        if (PInvoke.IsHungAppWindow(hwnd))
        {
            return true;
        }

        LRESULT answered = PInvoke.SendMessageTimeout(hwnd, PInvoke.WM_NULL, default, default,
            SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK, ResponseTimeoutMilliseconds);
        return answered.Value == 0 && Marshal.GetLastPInvokeError() == ErrorTimeout;
    }

    private static WindowState ToState(SHOW_WINDOW_CMD showCmd) => showCmd switch
    {
        SHOW_WINDOW_CMD.SW_SHOWMINIMIZED => WindowState.Minimized,
        SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED => WindowState.Maximized,
        _ => WindowState.Normal,
    };

    private static string Title(HWND hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        int length = PInvoke.GetWindowText(hwnd, buffer);
        return length > 0 ? new string(buffer[..length]) : string.Empty;
    }

    private static string ProcessName(HWND hwnd)
    {
        PInvoke.GetWindowThreadProcessId(hwnd, out uint processId);
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return string.Empty;
        }
    }
}
