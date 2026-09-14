using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>Small Win32 helpers for the WPF layer, which has no interop of its own.</summary>
public static class NativeWindow
{
    public const int WmHotkey = 0x0312;
    public const int WmDisplayChange = 0x007E;

    private const uint VkEscape = 0x1B;

    /// <summary>Global Esc for the confirmation window: reverts from any screen, even if the window has no picture.</summary>
    public static bool RegisterEscapeHotkey(nint hwnd, int id) =>
        PInvoke.RegisterHotKey(new HWND(hwnd), id, HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkEscape);

    /// <summary>
    /// A profile hotkey. <paramref name="modifiers"/> are <c>MOD_*</c> flags; auto-repeat is suppressed.
    /// <paramref name="error"/> is the Win32 error on failure (1409 = taken by another application).
    /// </summary>
    public static bool RegisterHotkey(nint hwnd, int id, int modifiers, int virtualKey, out int error)
    {
        bool registered = PInvoke.RegisterHotKey(new HWND(hwnd), id, (HOT_KEY_MODIFIERS)modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, (uint)virtualKey);
        error = registered ? 0 : Marshal.GetLastPInvokeError();
        return registered;
    }

    public static void UnregisterHotkey(nint hwnd, int id) => PInvoke.UnregisterHotKey(new HWND(hwnd), id);

    /// <summary>Centers the window on the primary monitor's work area, in physical pixels (per-monitor DPI aware).</summary>
    public static unsafe bool CenterOnPrimaryMonitor(nint hwnd)
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info) || !PInvoke.GetWindowRect(new HWND(hwnd), out RECT window))
        {
            return false;
        }

        RECT work = info.rcWork;
        int width = window.right - window.left;
        int height = window.bottom - window.top;
        int x = work.left + ((work.right - work.left - width) / 2);
        int y = work.top + ((work.bottom - work.top - height) / 2);

        return PInvoke.SetWindowPos(new HWND(hwnd), HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>Centers the window on a rectangle of the virtual desktop in physical pixels, e.g. a display's position and mode.</summary>
    public static bool CenterOnRect(nint hwnd, int left, int top, int width, int height)
    {
        if (!PInvoke.GetWindowRect(new HWND(hwnd), out RECT window))
        {
            return false;
        }

        int x = left + ((width - (window.right - window.left)) / 2);
        int y = top + ((height - (window.bottom - window.top)) / 2);
        return PInvoke.SetWindowPos(new HWND(hwnd), HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Moves the window next to the cursor – centered above it, clamped to the work area of the cursor's monitor – in
    /// physical pixels. Returns the new top-left corner, or null when a Win32 call failed.
    /// </summary>
    public static unsafe (int X, int Y)? PlaceNearCursor(nint hwnd)
    {
        if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
        {
            return null;
        }

        HMONITOR monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(monitor, &info) || !PInvoke.GetWindowRect(new HWND(hwnd), out RECT window))
        {
            return null;
        }

        RECT work = info.rcWork;
        int width = window.right - window.left;
        int height = window.bottom - window.top;
        int x = Math.Clamp(cursor.X - (width / 2), work.left, Math.Max(work.left, work.right - width));
        int y = Math.Clamp(cursor.Y - height, work.top, Math.Max(work.top, work.bottom - height));

        return PInvoke.SetWindowPos(new HWND(hwnd), HWND.Null, x, y, 0, 0,
            SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE)
            ? (x, y)
            : null;
    }

    /// <summary>Pixel size of a small icon such as the tray icon, at the current DPI of the primary monitor (where the tray is).</summary>
    public static int SmallIconSize()
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        uint dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : 96;
        return PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
    }

    /// <summary>Whether the taskbar uses the light theme – independent of the app theme, which can differ.</summary>
    public static bool IsTaskbarLight()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }
}
