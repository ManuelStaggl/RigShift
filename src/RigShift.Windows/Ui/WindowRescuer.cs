using System.Diagnostics;
using System.Globalization;
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
/// <see cref="IWindowRescuer"/> over <c>EnumWindows</c>: one of the user's windows (<see cref="TopLevelWindows.IsUserWindow"/>)
/// is lost when <c>MonitorFromRect</c> finds no monitor for it. Minimized and maximized windows are judged by their normal
/// position. The move uses <c>SetWindowPlacement</c>, so the window keeps its state.
/// </summary>
public sealed class WindowRescuer : IWindowRescuer
{
    /// <summary>The whole rescue must not hold up a switch for longer than this (analysis finding B-04).</summary>
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(3);

    private readonly ILogger _log;

    public WindowRescuer(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<WindowRescuer>();
    }

    public int RescueOffscreenWindows()
    {
        List<HWND> windows = TopLevelWindows.All();
        if (TopLevelWindows.PrimaryWorkspace() is not { } workspace)
        {
            _log.Warning("Primary monitor could not be read, no windows moved");
            return 0;
        }

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

            if (!TopLevelWindows.IsUserWindow(hwnd) || !TopLevelWindows.TryGetPlacement(hwnd, out WINDOWPLACEMENT placement))
            {
                continue;
            }

            PixelRect normal = workspace.ToScreen(placement.rcNormalPosition);
            WindowState state = TopLevelWindows.StateOf(placement.showCmd);
            PixelRect judged = normal;
            if (state == WindowState.Normal)
            {
                if (!PInvoke.GetWindowRect(hwnd, out RECT current))
                {
                    continue;
                }

                judged = TopLevelWindows.ToPixel(current);
            }

            if (judged.IsEmpty || !PInvoke.MonitorFromRect(TopLevelWindows.ToRect(judged), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL).IsNull)
            {
                continue;
            }

            if (TopLevelWindows.IsHung(hwnd))
            {
                _log.Warning("Lost window of {Process} not moved: the program is not responding", Describe(hwnd));
                continue;
            }

            if (Move(hwnd, placement, normal, state, workspace))
            {
                moved++;
            }
        }

        return moved;
    }

    private bool Move(HWND hwnd, WINDOWPLACEMENT placement, PixelRect normal, WindowState state, Workspace workspace)
    {
        PixelRect target = WindowGeometry.CenteredIn(normal, workspace.WorkArea);
        string process = Describe(hwnd);
        int error = TopLevelWindows.Place(hwnd, placement, target, state, workspace);
        if (error == TopLevelWindows.ErrorAccessDenied)
        {
            _log.Debug("Lost window of {Process} could not be moved: access denied (elevated?)", process);
            return false;
        }

        if (error != 0)
        {
            _log.Warning("Lost window of {Process} could not be moved (error {Error})", process, error);
            return false;
        }

        _log.Information("Moved lost window of {Process} ({State}) from {From} to the primary display at {To}",
            process, state, normal, target);
        return true;
    }

    private static string Describe(HWND hwnd) =>
        TopLevelWindows.ProcessName(hwnd, out uint processId) ?? "process " + processId.ToString(CultureInfo.InvariantCulture);
}
