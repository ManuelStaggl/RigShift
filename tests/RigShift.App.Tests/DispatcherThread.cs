using System.Windows.Threading;

namespace RigShift.App.Tests;

/// <summary>
/// A thread with a running WPF dispatcher, for services that capture <see cref="Dispatcher.CurrentDispatcher"/> when
/// they are created and come back to it from the pool.
/// </summary>
internal sealed class DispatcherThread : IDisposable
{
    private readonly Thread _thread;
    private Dispatcher? _dispatcher;

    public DispatcherThread()
    {
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            _dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        })
        {
            IsBackground = true,
            Name = "test-dispatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        ready.Wait();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public T Invoke<T>(Func<T> call) => _dispatcher!.Invoke(call);

    public void Invoke(Action call) => _dispatcher!.Invoke(call);

    /// <summary>Completes once everything queued so far has run.</summary>
    public void Drain() => _dispatcher!.Invoke(() => { }, DispatcherPriority.ContextIdle);

    public void Dispose()
    {
        _dispatcher!.InvokeShutdown();
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}
