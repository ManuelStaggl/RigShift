using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
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

            PixelRect bounds = workspace.ToScreen(placement.rcNormalPosition);
            if (bounds.IsEmpty)
            {
                continue;
            }

            windows.Add(new OpenWindow(
                (nint)hwnd.Value, ProcessName(hwnd), Title(hwnd), bounds, TopLevelWindows.StateOf(placement.showCmd)));
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

        if (TopLevelWindows.IsHung(hwnd))
        {
            _log.Warning("Window of {Process} not placed: the program is not responding", ProcessName(hwnd));
            return false;
        }

        if (!TopLevelWindows.TryGetPlacement(hwnd, out WINDOWPLACEMENT placement) || PrimaryWorkspace() is not { } workspace)
        {
            return false;
        }

        int error = TopLevelWindows.Place(hwnd, placement, bounds, state, workspace);
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
