using System.Collections.Concurrent;

namespace RigShift.App.Tests;

/// <summary>
/// Stands in for the UI thread: one thread that runs everything posted to it, as a dispatcher would. Lets a test tell
/// "ran on the UI thread" from "ran on whatever thread called".
/// </summary>
internal sealed class SingleThreadContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadContext()
    {
        _thread = new Thread(Pump) { IsBackground = true, Name = "test-ui" };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state)
    {
        // Fire-and-forget work of the app may come back after the test ended; there is nobody left to run it.
        try
        {
            _queue.Add((d, state));
        }
        catch (InvalidOperationException)
        {
        }
    }

    public override void Send(SendOrPostCallback d, object? state)
    {
        if (Environment.CurrentManagedThreadId == ThreadId)
        {
            d(state);
            return;
        }

        using var done = new ManualResetEventSlim();
        Post(_ =>
        {
            try
            {
                d(state);
            }
            finally
            {
                done.Set();
            }
        }, null);
        done.Wait();
    }

    /// <summary>Runs <paramref name="create"/> on the thread, the way a singleton is created during startup.</summary>
    public T Create<T>(Func<T> create)
    {
        T value = default!;
        Send(_ => value = create(), null);
        return value;
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(TimeSpan.FromSeconds(5));
        _queue.Dispose();
    }

    private void Pump()
    {
        SetSynchronizationContext(this);
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}
