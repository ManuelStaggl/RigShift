using System.Windows.Interop;
using System.Windows.Threading;
using RigShift.Windows.Ui;

namespace RigShift.App.Services;

/// <summary>Hidden top-level window that receives <c>WM_DISPLAYCHANGE</c> (message-only windows do not get broadcasts).</summary>
public sealed class DisplayChangeWatcher : IDisposable
{
    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;

    public DisplayChangeWatcher()
    {
        _source = new HwndSource(new HwndSourceParameters("RigShift.DisplayChangeWatcher") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);

        // A topology change sends a burst of messages; react once when it has settled.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? DisplaysChanged;

    public void Dispose()
    {
        _debounce.Stop();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmDisplayChange)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        return 0;
    }
}
