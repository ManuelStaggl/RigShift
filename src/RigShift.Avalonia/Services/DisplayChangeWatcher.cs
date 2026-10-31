using Avalonia.Threading;
using RigShift.Windows.Ui;

namespace RigShift.Avalonia.Services;

/// <summary>Hidden top-level window that receives <c>WM_DISPLAYCHANGE</c> (message-only windows do not get broadcasts).</summary>
internal sealed class DisplayChangeWatcher : IDisposable
{
    private readonly MessageWindow _window = new(receivesBroadcasts: true);

    // A topology change sends a burst of messages; react once when it has settled.
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public DisplayChangeWatcher()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        };
        _window.MessageReceived += (_, message) =>
        {
            if (message.Message == NativeWindow.WmDisplayChange)
            {
                _debounce.Stop();
                _debounce.Start();
            }
        };
    }

    public event EventHandler? DisplaysChanged;

    /// <summary>A top-level window of the app that always exists, e.g. for the reason Windows shows when logging off.</summary>
    public nint Handle => _window.Handle;

    public void Dispose()
    {
        _debounce.Stop();
        _window.Dispose();
    }
}
