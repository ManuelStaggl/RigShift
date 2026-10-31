using System.Runtime.InteropServices;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.Shell.Common;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>Small Win32 helpers for the WPF layer, which has no interop of its own.</summary>
public static class NativeWindow
{
    public const int WmHotkey = 0x0312;
    public const int WmDisplayChange = 0x007E;

    private const uint VkEscape = 0x1B;

    /// <summary>What Windows shows next to RigShift while it holds up logging off or shutting down.</summary>
    public static void ExplainShutdownBlock(nint hwnd, string reason) => PInvoke.ShutdownBlockReasonCreate(new HWND(hwnd), reason);

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

    /// <summary>
    /// The name the current keyboard layout gives a key, e.g. "," or "Page Up"; <c>null</c> if Windows has none.
    /// </summary>
    public static string? KeyName(int virtualKey)
    {
        uint scanCode = PInvoke.MapVirtualKey((uint)virtualKey, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
        if (scanCode == 0)
        {
            return null;
        }

        // Bit 24 marks the extended keys; without it Page Up reads as the number pad's 9.
        int lParam = (int)(scanCode << 16) | (IsExtendedKey(virtualKey) ? 1 << 24 : 0);
        Span<char> buffer = stackalloc char[64];
        int length = PInvoke.GetKeyNameText(lParam, buffer);
        return length > 0 ? new string(buffer[..length]) : null;
    }

    /// <summary>Page Up/Down, End, Home, arrows, Insert, Delete, Print, Windows and menu keys, Num Lock, number pad divide.</summary>
    private static bool IsExtendedKey(int virtualKey) =>
        virtualKey is (>= 0x21 and <= 0x28) or 0x2C or 0x2D or 0x2E or 0x5B or 0x5C or 0x5D or 0x6F or 0x90;

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

    /// <summary>
    /// Brings a window that does not fit its monitor's work area back onto a screen. WPF's <c>CenterScreen</c> opens the
    /// window on the monitor with the cursor, whatever its size: 1280 × 780 at 150 % is 1920 × 1170 pixels, taller than a
    /// 1080p monitor. If the window's intended size fits there, it is centered on that monitor; if not, it goes to the
    /// primary monitor when it fits there, and otherwise takes the whole work area. <paramref name="width"/> and
    /// <paramref name="height"/> are the intended size in device-independent units, each monitor gets it at its own DPI.
    /// </summary>
    /// <returns>
    /// The new bounds in physical pixels and whether the window went to the primary monitor, or null when it already
    /// fit or a Win32 call failed.
    /// </returns>
    public static (System.Drawing.Rectangle Bounds, bool ToPrimary)? FitIntoMonitor(nint hwnd, double width, double height)
    {
        var handle = new HWND(hwnd);
        HMONITOR monitor = PInvoke.MonitorFromWindow(handle, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        if (!PInvoke.GetWindowRect(handle, out RECT window) || WorkArea(monitor) is not { } work)
        {
            return null;
        }

        uint dpi = PInvoke.GetDpiForWindow(handle);
        System.Drawing.Rectangle primaryWork = monitor == primary ? default : WorkArea(primary) ?? default;
        if (Place(ToRectangle(window), work, Scaled(width, height, dpi), primaryWork, Scaled(width, height, MonitorDpi(primary) ?? dpi))
            is not { } placed)
        {
            return null;
        }

        System.Drawing.Rectangle bounds = placed.Bounds;
        if (!Move(handle, bounds))
        {
            return null;
        }

        // A primary monitor with other scaling: WPF has just resized the window for the new DPI by Windows' suggestion,
        // which starts from the old size – so the bounds go on once more.
        if (placed.ToPrimary && PInvoke.GetDpiForWindow(handle) != dpi)
        {
            Move(handle, bounds);
        }

        return placed;
    }

    /// <summary>
    /// Opens a window on the monitor with the cursor, centered at its intended size – or, when it does not fit there, on
    /// the primary monitor if it fits there, and otherwise in the whole work area. The same rules as
    /// <see cref="FitIntoMonitor"/>, for a window that has no place yet.
    /// </summary>
    /// <returns>The bounds in physical pixels and whether the window went to the primary monitor, or null when a Win32 call failed.</returns>
    public static (System.Drawing.Rectangle Bounds, bool ToPrimary)? OpenOnCursorMonitor(nint hwnd, double width, double height)
    {
        var handle = new HWND(hwnd);
        if (!PInvoke.GetCursorPos(out System.Drawing.Point cursor))
        {
            return null;
        }

        HMONITOR monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        HMONITOR primary = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        if (WorkArea(monitor) is not { } work)
        {
            return null;
        }

        uint dpi = MonitorDpi(monitor) ?? PInvoke.GetDpiForWindow(handle);
        System.Drawing.Rectangle primaryWork = monitor == primary ? default : WorkArea(primary) ?? default;
        (System.Drawing.Rectangle bounds, bool toPrimary) = Choose(work, Scaled(width, height, dpi), primaryWork, Scaled(width, height, MonitorDpi(primary) ?? dpi));
        if (!Move(handle, bounds))
        {
            return null;
        }

        // Another monitor's scaling: the window was just resized for the new DPI by Windows' suggestion, which starts
        // from the old size – so the bounds go on once more.
        if (PInvoke.GetDpiForWindow(handle) != dpi || toPrimary)
        {
            Move(handle, bounds);
        }

        return (bounds, toPrimary);
    }

    /// <summary>
    /// Where a window that sticks out of <paramref name="work"/> belongs. <paramref name="primaryWork"/> is empty when the
    /// window is on the primary monitor already. Null when it lies inside its work area.
    /// </summary>
    internal static (System.Drawing.Rectangle Bounds, bool ToPrimary)? Place(
        System.Drawing.Rectangle window,
        System.Drawing.Rectangle work,
        System.Drawing.Size wanted,
        System.Drawing.Rectangle primaryWork,
        System.Drawing.Size wantedOnPrimary)
    {
        return work.IsEmpty || work.Contains(window) ? null : Choose(work, wanted, primaryWork, wantedOnPrimary);
    }

    /// <summary>Centered on <paramref name="work"/>, or on the primary monitor when the window fits only there.</summary>
    private static (System.Drawing.Rectangle Bounds, bool ToPrimary) Choose(
        System.Drawing.Rectangle work,
        System.Drawing.Size wanted,
        System.Drawing.Rectangle primaryWork,
        System.Drawing.Size wantedOnPrimary)
    {
        bool fitsHere = wanted.Width <= work.Width && wanted.Height <= work.Height;
        bool fitsPrimary = !primaryWork.IsEmpty && wantedOnPrimary.Width <= primaryWork.Width && wantedOnPrimary.Height <= primaryWork.Height;
        return !fitsHere && fitsPrimary
            ? (Center(primaryWork, wantedOnPrimary), true)
            : (Center(work, wanted), false);
    }

    private static System.Drawing.Rectangle Center(System.Drawing.Rectangle work, System.Drawing.Size size)
    {
        int width = Math.Clamp(size.Width, 1, work.Width);
        int height = Math.Clamp(size.Height, 1, work.Height);
        return new System.Drawing.Rectangle(work.X + ((work.Width - width) / 2), work.Y + ((work.Height - height) / 2), width, height);
    }

    private static System.Drawing.Size Scaled(double width, double height, uint dpi)
    {
        double scale = (dpi == 0 ? 96 : dpi) / 96d;
        return new System.Drawing.Size((int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    private static bool Move(HWND handle, System.Drawing.Rectangle bounds) =>
        PInvoke.SetWindowPos(handle, HWND.Null, bounds.X, bounds.Y, bounds.Width, bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);

    private static unsafe System.Drawing.Rectangle? WorkArea(HMONITOR monitor)
    {
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        return PInvoke.GetMonitorInfo(monitor, &info) ? ToRectangle(info.rcWork) : null;
    }

    private static uint? MonitorDpi(HMONITOR monitor) =>
        PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : null;

    private static System.Drawing.Rectangle ToRectangle(RECT rect) =>
        System.Drawing.Rectangle.FromLTRB(rect.left, rect.top, rect.right, rect.bottom);

    /// <summary>
    /// The Windows "Open" dialog, modal to <paramref name="owner"/>. WinRT's FileOpenPicker refuses to open in an
    /// elevated process, which RigShift may be; the shell dialog behind it does not.
    /// </summary>
    /// <param name="filter">WPF style, e.g. "Programs (*.exe)|*.exe|All files (*.*)|*.*".</param>
    /// <returns>The chosen file, or <c>null</c> when the user cancelled.</returns>
    public static string? PickFile(nint owner, string filter, string title, string? initialFolder)
    {
        ArgumentNullException.ThrowIfNull(title);
        return ShowFileDialog(
            (IFileDialog)new FileOpenDialog(),
            owner,
            filter,
            title,
            FILEOPENDIALOGOPTIONS.FOS_FILEMUSTEXIST | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR,
            initialFolder,
            fileName: null);
    }

    /// <summary>The Windows "Save as" dialog, modal to <paramref name="owner"/>; it asks before replacing a file.</summary>
    /// <param name="filter">WPF style; the first pattern's extension is added when the user types none.</param>
    /// <param name="title">The dialog's caption; <c>null</c> keeps Windows' own "Save as".</param>
    /// <returns>The chosen file, or <c>null</c> when the user cancelled.</returns>
    public static string? SaveFile(nint owner, string filter, string? title, string suggestedName)
    {
        ArgumentNullException.ThrowIfNull(suggestedName);
        return ShowFileDialog(
            (IFileDialog)new FileSaveDialog(),
            owner,
            filter,
            title,
            FILEOPENDIALOGOPTIONS.FOS_OVERWRITEPROMPT | FILEOPENDIALOGOPTIONS.FOS_FORCEFILESYSTEM | FILEOPENDIALOGOPTIONS.FOS_NOCHANGEDIR,
            initialFolder: null,
            suggestedName);
    }

    private static unsafe string? ShowFileDialog(
        IFileDialog dialog, nint owner, string filter, string? title, FILEOPENDIALOGOPTIONS options, string? initialFolder, string? fileName)
    {
        ArgumentNullException.ThrowIfNull(filter);
        string[] parts = filter.Split('|');
        var specs = new COMDLG_FILTERSPEC[parts.Length / 2];
        var pins = new List<GCHandle>();
        try
        {
            for (int i = 0; i < specs.Length; i++)
            {
                GCHandle name = GCHandle.Alloc(parts[2 * i], GCHandleType.Pinned);
                GCHandle pattern = GCHandle.Alloc(parts[(2 * i) + 1], GCHandleType.Pinned);
                pins.Add(name);
                pins.Add(pattern);
                specs[i] = new() { pszName = (char*)name.AddrOfPinnedObject(), pszSpec = (char*)pattern.AddrOfPinnedObject() };
            }

            fixed (COMDLG_FILTERSPEC* specsPtr = specs)
            {
                dialog.SetFileTypes((uint)specs.Length, specsPtr);
            }

            if (title is not null)
            {
                fixed (char* titlePtr = title)
                {
                    dialog.SetTitle(titlePtr);
                }
            }

            if (fileName is not null)
            {
                // "*.zip" → "zip": without a default extension a name typed without one is saved as is.
                string extension = specs.Length > 0 ? Path.GetExtension(parts[1]).TrimStart('.') : string.Empty;
                fixed (char* namePtr = fileName)
                fixed (char* extensionPtr = extension)
                {
                    dialog.SetFileName(namePtr);
                    dialog.SetDefaultExtension(extensionPtr);
                }
            }

            dialog.SetOptions(options);
            if (initialFolder is not null && Directory.Exists(initialFolder))
            {
                Guid shellItem = typeof(IShellItem).GUID;
                fixed (char* folderPtr = initialFolder)
                {
                    if (PInvoke.SHCreateItemFromParsingName(folderPtr, null, &shellItem, out object folder).Succeeded)
                    {
                        dialog.SetFolder((IShellItem)folder);
                    }
                }
            }

            try
            {
                dialog.Show(new HWND(owner));
            }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x800704C7))
            {
                return null; // ERROR_CANCELLED
            }

            dialog.GetResult(out IShellItem result);
            PWSTR path;
            result.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, &path);
            try
            {
                return path.ToString();
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)path.Value);
            }
        }
        finally
        {
            foreach (GCHandle pin in pins)
            {
                pin.Free();
            }

            Marshal.FinalReleaseComObject(dialog);
        }
    }

    /// <summary>Physical pixels per device-independent unit on the window's monitor.</summary>
    public static double DpiScale(nint hwnd) => (PInvoke.GetDpiForWindow(new HWND(hwnd)) is var dpi and > 0 ? dpi : 96) / 96d;

    /// <summary>Pixel size of a small icon such as the tray icon, at the current DPI of the primary monitor (where the tray is).</summary>
    public static int SmallIconSize()
    {
        HMONITOR monitor = PInvoke.MonitorFromPoint(default, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        uint dpi = PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpiX, out _).Succeeded ? dpiX : 96;
        return PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
    }

    /// <summary>A Windows system colour (GetSysColor), for high contrast: the user's own colours, as 0xRRGGBB.</summary>
    public static uint SystemColor(SystemColorKind kind)
    {
        uint bgr = PInvoke.GetSysColor((SYS_COLOR_INDEX)kind);
        return ((bgr & 0xFF) << 16) | (bgr & 0xFF00) | ((bgr >> 16) & 0xFF);
    }

    /// <summary>Whether the taskbar uses the light theme – independent of the app theme, which can differ.</summary>
    public static bool IsTaskbarLight()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
    }
}

/// <summary>The system colours high contrast needs, with their GetSysColor index.</summary>
public enum SystemColorKind
{
    Window = 5,

    WindowText = 8,

    Highlight = 13,

    HighlightText = 14,

    ButtonFace = 15,

    GrayText = 17,

    HotLight = 26,
}
