using System.Collections.Concurrent;
using RigShift.App.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class CrashHandlingTests
{
    private readonly ManualTime _time = new();

    [Fact]
    public void Tracker_FirstException_IsToldToTheUser()
    {
        var tracker = new UiExceptionTracker(_time);

        tracker.Record(new InvalidOperationException("layout")).ShouldBe(UiExceptionVerdict.Notify);
    }

    /// <summary>
    /// Every UI exception used to be marked handled. One thrown from a layout pass or a timer comes back at once, so the
    /// app spun on it for good – window dead, log growing, nothing said.
    /// </summary>
    [Fact]
    public void Tracker_SameExceptionAgainAndAgain_EscalatesOnce()
    {
        var tracker = new UiExceptionTracker(_time);
        var verdicts = new List<UiExceptionVerdict>();

        for (int i = 0; i < 6; i++)
        {
            verdicts.Add(tracker.Record(new InvalidOperationException("layout")));
            _time.Advance(TimeSpan.FromMilliseconds(200));
        }

        verdicts.ShouldBe([
            UiExceptionVerdict.Notify, UiExceptionVerdict.Quiet, UiExceptionVerdict.Escalate,
            UiExceptionVerdict.Quiet, UiExceptionVerdict.Quiet, UiExceptionVerdict.Quiet,
        ]);
    }

    [Fact]
    public void Tracker_TheSameExceptionNowAndThen_NeverEscalates()
    {
        var tracker = new UiExceptionTracker(_time);

        for (int i = 0; i < 10; i++)
        {
            tracker.Record(new InvalidOperationException("layout")).ShouldBe(UiExceptionVerdict.Notify);
            _time.Advance(TimeSpan.FromMinutes(1));
        }
    }

    [Fact]
    public void Tracker_ManyDifferentExceptionsAtOnce_EscalateToo()
    {
        var tracker = new UiExceptionTracker(_time);
        var verdicts = new List<UiExceptionVerdict>();

        for (int i = 0; i < UiExceptionTracker.AnyLimit; i++)
        {
            verdicts.Add(tracker.Record(new InvalidOperationException($"number {i}")));
        }

        verdicts[^1].ShouldBe(UiExceptionVerdict.Escalate);
        verdicts.Count(v => v == UiExceptionVerdict.Escalate).ShouldBe(1);
    }

    [Fact]
    public void Tracker_AfterTheUserChoseToGoOn_StartsCountingAnew()
    {
        var tracker = new UiExceptionTracker(_time);
        for (int i = 0; i < UiExceptionTracker.SameLimit; i++)
        {
            tracker.Record(new InvalidOperationException("layout"));
        }

        tracker.Reset();

        tracker.Record(new InvalidOperationException("layout")).ShouldBe(UiExceptionVerdict.Notify);
    }

    /// <summary>A crash on a pool thread ended the process without a line in the log.</summary>
    [Fact]
    public void UnhandledException_IsLoggedAsFatal()
    {
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();

        CrashLogging.OnUnhandled(log, new UnhandledExceptionEventArgs(new InvalidOperationException("pool thread"), isTerminating: true));

        LogEvent logged = sink.Events.ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogEventLevel.Fatal);
        logged.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    [Fact]
    public void UnobservedTaskException_IsLoggedAndObserved()
    {
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new InvalidOperationException("forgotten task")));

        CrashLogging.OnUnobserved(log, args);

        args.Observed.ShouldBeTrue();
        LogEvent logged = sink.Events.ShouldHaveSingleItem();
        logged.Level.ShouldBe(LogEventLevel.Error);
        logged.Exception.ShouldBeOfType<InvalidOperationException>();
    }

    /// <summary>A cancelled fire-and-forget task is no error, and the log should not claim one.</summary>
    [Fact]
    public void UnobservedTaskException_ThatIsOnlyACancellation_IsNotAnError()
    {
        var sink = new CollectingSink();
        using Logger log = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();
        var args = new UnobservedTaskExceptionEventArgs(new AggregateException(new TaskCanceledException()));

        CrashLogging.OnUnobserved(log, args);

        args.Observed.ShouldBeTrue();
        sink.Events.ShouldAllBe(e => e.Level < LogEventLevel.Warning);
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class CollectingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> _events = new();

        public IReadOnlyCollection<LogEvent> Events => _events.ToArray();

        public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
    }
}
