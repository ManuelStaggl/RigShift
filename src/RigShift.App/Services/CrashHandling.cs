using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The last line of a process that dies: without these handlers an exception on a pool thread or in <c>Main</c> ended
/// RigShift with nothing in the log, which is the one place anybody would look.
/// </summary>
internal static class CrashLogging
{
    public static void Install(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            OnUnhandled(log, e);

            // The process ends right after this handler; what is still buffered has to reach the file first.
            Log.CloseAndFlush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) => OnUnobserved(log, e);
    }

    internal static void OnUnhandled(ILogger log, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            log.Fatal(ex, "Unhandled exception, RigShift is {Fate}", e.IsTerminating ? "ending" : "going on");
        }
        else
        {
            log.Fatal("Unhandled non-CLR exception {Exception}, RigShift is {Fate}", e.ExceptionObject, e.IsTerminating ? "ending" : "going on");
        }
    }

    internal static void OnUnobserved(ILogger log, UnobservedTaskExceptionEventArgs e)
    {
        // Observed either way: a forgotten task is a bug to fix, not a reason to take the app down.
        e.SetObserved();
        Exception shown = e.Exception.InnerExceptions.Count == 1 ? e.Exception.InnerExceptions[0] : e.Exception;
        if (e.Exception.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            log.Debug(shown, "A cancelled task was never awaited");
            return;
        }

        log.Error(shown, "A task failed and nobody looked at its result");
    }
}

/// <summary>What to do with one UI exception.</summary>
internal enum UiExceptionVerdict
{
    /// <summary>Tell the user in passing and go on.</summary>
    Notify,

    /// <summary>A repeat of something just told; the log has it, the user has heard.</summary>
    Quiet,

    /// <summary>It keeps coming back: stop swallowing it and ask the user.</summary>
    Escalate,
}

/// <summary>
/// Tells one UI exception from one that comes back at once. Marking every exception handled keeps the app alive after a
/// slip in a click handler – and keeps it spinning for good when the exception comes from a layout pass or a timer.
/// UI thread only.
/// </summary>
internal sealed class UiExceptionTracker(TimeProvider time)
{
    /// <summary>The same exception this often within <see cref="Window"/> is a loop.</summary>
    public const int SameLimit = 3;

    /// <summary>This many exceptions of any kind within <see cref="Window"/> mean the UI is not working either.</summary>
    public const int AnyLimit = 10;

    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly List<(DateTimeOffset At, string Key)> _recent = [];
    private bool _escalated;

    public UiExceptionVerdict Record(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        DateTimeOffset now = time.GetUtcNow();
        _recent.RemoveAll(r => now - r.At > Window);

        string key = $"{exception.GetType().FullName}|{exception.Message}|{exception.TargetSite?.Name}";
        _recent.Add((now, key));
        if (_escalated)
        {
            return UiExceptionVerdict.Quiet;
        }

        int same = _recent.Count(r => r.Key == key);
        if (same >= SameLimit || _recent.Count >= AnyLimit)
        {
            _escalated = true;
            return UiExceptionVerdict.Escalate;
        }

        return same == 1 ? UiExceptionVerdict.Notify : UiExceptionVerdict.Quiet;
    }

    /// <summary>The user chose to go on; the next exception is a new story.</summary>
    public void Reset()
    {
        _recent.Clear();
        _escalated = false;
    }
}
