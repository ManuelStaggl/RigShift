using System.Diagnostics;
using System.Runtime.InteropServices;
using RigShift.Core.Abstractions;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>
/// <see cref="IWindowRescuer"/> over <c>EnumWindows</c>: a visible, uncloaked top-level window without the tool-window
/// style is lost when <c>MonitorFromRect</c> finds no monitor for it. Minimized and maximized windows are judged by
/// their normal position. The move uses <c>SetWindowPlacement</c>, so the window keeps its state.
/// </summary>
public sealed class WindowRescuer : IWindowRescuer
{
    private const int ErrorAccessDenied = 5;
    private const int ErrorTimeout = 1460;
    private const uint ResponseTimeoutMilliseconds = 250;

    /// <summary>The whole rescue must not hold up a switch for longer than this (analysis finding B-04).</summary>
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(3);

    private readonly ILogger _log;

    public WindowRescuer(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<WindowRescuer>();
    }

    public unsafe int RescueOffscreenWindows()
    {
        var windows = new List<HWND>();
        PInvoke.EnumWindows((hwnd, _) =>
        {
            windows.Add(hwnd);
            return true;
        }, default);

        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(primary, &info))
        {
            _log.Warning("Primary monitor could not be read, no windows moved");
            return 0;
        }

        // WINDOWPLACEMENT uses workspace coordinates: screen coordinates shifted by the primary work area's offset.
        int offsetX = info.rcWork.left - info.rcMonitor.left;
        int offsetY = info.rcWork.top - info.rcMonitor.top;
        PixelRect workArea = ToPixel(info.rcWork);

        int moved = 0;
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < windows.Count; index++)
        {
            HWND hwnd = windows[index];
            if (Stopwatch.GetElapsedTime(started) > TimeLimit)
            {
                _log.Warning("Moving lost windows stopped after {Seconds:0.0} s, {Remaining} window(s) not checked",
                    Stopwatch.GetElapsedTime(started).TotalSeconds, windows.Count - index);
                break;
            }

            if (!IsCandidate(hwnd))
            {
                continue;
            }

            var placement = new WINDOWPLACEMENT { length = (uint)sizeof(WINDOWPLACEMENT) };
            if (!PInvoke.GetWindowPlacement(hwnd, ref placement))
            {
                continue;
            }

            PixelRect normal = ToPixel(placement.rcNormalPosition);
            var normalOnScreen = new PixelRect(normal.Left + offsetX, normal.Top + offsetY, normal.Right + offsetX, normal.Bottom + offsetY);
            bool minimizedOrMaximized = placement.showCmd is SHOW_WINDOW_CMD.SW_SHOWMINIMIZED or SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED;
            PixelRect judged = normalOnScreen;
            if (!minimizedOrMaximized)
            {
                if (!PInvoke.GetWindowRect(hwnd, out RECT current))
                {
                    continue;
                }

                judged = ToPixel(current);
            }

            if (judged.IsEmpty || !PInvoke.MonitorFromRect(ToRect(judged), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull)
            {
                continue;
            }

            // SetWindowPlacement waits for the window's thread; a hung program (shader compile) would stall the switch.
            if (IsHung(hwnd))
            {
                _log.Warning("Lost window of {Process} not moved: the program is not responding", ProcessName(hwnd));
                continue;
            }

            if (Move(hwnd, placement, normalOnScreen, workArea, offsetX, offsetY))
            {
                moved++;
            }
        }

        return moved;
    }

    private static unsafe bool IsCandidate(HWND hwnd)
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
    /// Hung as Windows sees it (no message processed for 5 s), or no answer to <c>WM_NULL</c> within 250 ms. A refused
    /// message (elevated window) is not "hung" – <c>SetWindowPlacement</c> reports that itself.
    /// </summary>
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

    private bool Move(HWND hwnd, WINDOWPLACEMENT placement, PixelRect normalOnScreen, PixelRect workArea, int offsetX, int offsetY)
    {
        PixelRect target = WindowGeometry.CenteredIn(normalOnScreen, workArea);
        SHOW_WINDOW_CMD state = placement.showCmd;
        placement.rcNormalPosition = ToRect(new PixelRect(target.Left - offsetX, target.Top - offsetY, target.Right - offsetX, target.Bottom - offsetY));

        // Moving a window must not steal the focus; a maximized window has no non-activating variant.
        placement.showCmd = state switch
        {
            SHOW_WINDOW_CMD.SW_SHOWMINIMIZED => SHOW_WINDOW_CMD.SW_SHOWMINNOACTIVE,
            SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED => SHOW_WINDOW_CMD.SW_SHOWMAXIMIZED,
            _ => SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE,
        };

        string process = ProcessName(hwnd);
        if (!PInvoke.SetWindowPlacement(hwnd, in placement))
        {
            int error = Marshal.GetLastPInvokeError();
            if (error == ErrorAccessDenied)
            {
                _log.Debug("Lost window of {Process} could not be moved: access denied (elevated?)", process);
            }
            else
            {
                _log.Warning("Lost window of {Process} could not be moved (error {Error})", process, error);
            }

            return false;
        }

        _log.Information("Moved lost window of {Process} ({State}) from {From} to the primary display at {To}",
            process, state, normalOnScreen, target);
        return true;
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
            return "process " + processId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static PixelRect ToPixel(RECT rect) => new(rect.left, rect.top, rect.right, rect.bottom);

    private static RECT ToRect(PixelRect rect) => new() { left = rect.Left, top = rect.Top, right = rect.Right, bottom = rect.Bottom };
}
