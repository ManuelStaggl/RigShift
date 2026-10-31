using RigShift.Core.Abstractions;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;

namespace RigShift.Windows.Ui;

/// <summary>
/// <see cref="ICursorConfinement"/> through <c>ClipCursor</c>. The clip is a system-wide state that outlives the call,
/// so it is checked four times a second rather than set once: Alt+Tab, the Windows key or a window popping up free the
/// cursor at the next look, and Windows itself drops the clip on some focus changes, which the next look puts back.
/// Freed when RigShift exits or crashes, as far as .NET still runs code then.
/// </summary>
public sealed class CursorConfinement : ICursorConfinement
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly ILogger _log;

    public CursorConfinement(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<CursorConfinement>();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Free();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => Free();
    }

    public IDisposable KeepOnWindowOf(string processName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        _log.Information("Keeping the mouse on the display of {Process} while it is in front", processName);
        return new Confinement(processName, _log);
    }

    private static unsafe void Free() => PInvoke.ClipCursor((RECT*)null);

    private sealed class Confinement : IDisposable
    {
        private readonly string _processName;
        private readonly ILogger _log;
        private readonly Timer _timer;
        private readonly Lock _lock = new();
        private HWND _lastWindow;
        private bool _lastIsGame;
        private bool _clipped;
        private bool _disposed;

        public Confinement(string processName, ILogger log)
        {
            _processName = processName;
            _log = log;
            _timer = new Timer(_ => Look(), null, TimeSpan.Zero, PollInterval);
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _timer.Dispose();
                Free();
            }

            _log.Information("Mouse free again: the session of {Process} ended", _processName);
        }

        private unsafe void Look()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }

                HWND front = PInvoke.GetForegroundWindow();
                if (front != _lastWindow)
                {
                    _lastWindow = front;
                    _lastIsGame = front != default
                        && string.Equals(TopLevelWindows.ProcessName(front, out _), _processName, StringComparison.OrdinalIgnoreCase);
                }

                var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
                if (_lastIsGame && PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(front, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST), &info))
                {
                    // Again on every look: Windows may have dropped it since.
                    RECT display = info.rcMonitor;
                    PInvoke.ClipCursor(&display);
                    _clipped = true;
                }
                else if (_clipped)
                {
                    Free();
                    _clipped = false;
                }
            }
        }
    }
}
