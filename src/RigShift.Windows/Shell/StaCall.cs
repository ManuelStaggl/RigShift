namespace RigShift.Windows.Shell;

/// <summary>
/// One call on an apartment thread of its own, with a time limit. The shell answers from Explorer's process; while
/// Explorer hangs or restarts the call does not come back, and whoever waits for it without a limit hangs with it.
/// </summary>
internal static class StaCall
{
    /// <summary>
    /// <c>Finished</c> is false when <paramref name="work"/> was still out after <paramref name="limit"/>; the thread is
    /// a background thread and is left behind. Exceptions of the work come back as <c>Failure</c>.
    /// </summary>
    public static (bool Finished, T? Value, Exception? Failure) Run<T>(Func<T?> work, TimeSpan limit)
        where T : class
    {
        var outcome = new Outcome<T>();
        var thread = new Thread(() =>
        {
            try
            {
                outcome.Value = work();
            }
#pragma warning disable CA1031 // The caller decides which failures it expects; nothing may escape a bare thread.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                outcome.Failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "RigShift shell call",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        // Only a joined thread has published its fields; an abandoned one may still write them.
        return thread.Join(limit) ? (true, outcome.Value, outcome.Failure) : (false, null, null);
    }

    private sealed class Outcome<T>
        where T : class
    {
        public T? Value { get; set; }

        public Exception? Failure { get; set; }
    }
}
