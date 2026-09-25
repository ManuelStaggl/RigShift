namespace RigShift.App.Services;

/// <summary>
/// The thread something was made on – the UI thread in the app – and the way back to it. Compares threads, not
/// contexts: WPF hands out a new synchronization context per dispatcher operation, so a context comparison never matches
/// and "right away" silently became "later" (v4 finding A-07).
/// </summary>
internal sealed class UiThread
{
    private readonly SynchronizationContext? _context = SynchronizationContext.Current;
    private readonly int _thread = Environment.CurrentManagedThreadId;

    /// <summary>Runs <paramref name="action"/> right away on the UI thread (or where there is none, in tests), else posts it there.</summary>
    public void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (_context is null || Environment.CurrentManagedThreadId == _thread)
        {
            action();
        }
        else
        {
            _context.Post(_ => action(), null);
        }
    }
}
