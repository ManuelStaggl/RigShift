namespace RigShift.App.Tests;

/// <summary>
/// A thread that stands in for the app's UI thread, for services that capture the synchronization context when they
/// are created and come back to it from the pool.
/// </summary>
internal sealed class DispatcherThread : IDisposable
{
    private readonly SingleThreadContext _context = new();

    public int ThreadId => _context.ThreadId;

    public T Invoke<T>(Func<T> call) => _context.Create(call);

    public void Invoke(Action call) => _context.Send(_ => call(), null);

    /// <summary>Runs an async body on the thread; its awaits come back to it, as they do for a view model in the app.</summary>
    public Task RunAsync(Func<Task> body)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(
            async _ =>
            {
                try
                {
                    await body();
                    done.SetResult();
                }
                catch (Exception ex)
                {
                    done.SetException(ex);
                }
            },
            null);
        return done.Task;
    }

    /// <summary>Completes once everything queued so far, and what that queued in turn, has run.</summary>
    public void Drain()
    {
        do
        {
            Invoke(() => { });
        }
        while (_context.Pending > 0);
    }

    public void Dispose() => _context.Dispose();
}
