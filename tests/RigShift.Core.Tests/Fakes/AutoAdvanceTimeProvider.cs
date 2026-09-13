namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// Virtual clock whose timers fire at once: creating a timer advances the clock by its due time.
/// Makes wait loops instantaneous and deterministic while still measuring how long they would have waited.
/// </summary>
internal sealed class AutoAdvanceTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Start = new(2026, 9, 13, 13, 32, 0, TimeSpan.Zero);

    private readonly Lock _gate = new();
    private DateTimeOffset _now = Start;

    public TimeSpan Elapsed => GetUtcNow() - Start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                _now += dueTime;
            }

            // Fire asynchronously: Task.Delay stores the timer after CreateTimer returns.
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new InertTimer();
    }

    private sealed class InertTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
