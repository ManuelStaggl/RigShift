using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Core.Profiles;

/// <summary>
/// Puts the helper windows back where they belong. Runs after the programs were started and before the game, and it
/// waits: a dashboard tool needs a few seconds to show its window, and placing a window that is not there yet does
/// nothing at all.
/// </summary>
public sealed class WindowLayoutRestorer(IWindowLayout windows, TimeProvider time, ILogger log)
{
    private readonly IWindowLayout _windows = windows;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log.ForContext<WindowLayoutRestorer>();

    /// <summary>
    /// Waits for the saved windows to turn up, then places each one. Stops waiting as soon as every window is there,
    /// and gives up on the stragglers after <see cref="WindowLayout.WindowWait"/> instead of holding up the game.
    /// </summary>
    public async Task<WindowLayoutResult> RestoreAsync(WindowLayout layout, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.IsEmpty)
        {
            return new WindowLayoutResult(0, 0, 0);
        }

        DateTimeOffset deadline = _time.GetUtcNow() + WindowLayout.WindowWait;
        IReadOnlyList<WindowMatch> matches;
        while (true)
        {
            matches = WindowLayoutMatching.Match(layout.Windows, _windows.Open());
            if (matches.All(m => m.Window is not null) || _time.GetUtcNow() >= deadline)
            {
                break;
            }

            await Task.Delay(WindowLayout.PollInterval, _time, cancellationToken);
        }

        int placed = 0;
        int refused = 0;
        int missing = 0;
        foreach (WindowMatch match in matches)
        {
            if (match.Window is not { } window)
            {
                missing++;
                _log.Information("No window of {Process} turned up within {Seconds} s, its position was left alone",
                    match.Placement.ProcessName, WindowLayout.WindowWait.TotalSeconds);
                continue;
            }

            if (_windows.Place(window.Handle, match.Placement.Bounds, match.Placement.State))
            {
                placed++;
            }
            else
            {
                refused++;
            }
        }

        _log.Information("Window layout: {Placed} placed, {Refused} refused, {Missing} not open", placed, refused, missing);
        return new WindowLayoutResult(placed, refused, missing);
    }
}

/// <summary>What became of a saved window layout.</summary>
/// <param name="Placed">Windows put back where they belong.</param>
/// <param name="Refused">Windows that are open but could not be moved – elevated, or not responding.</param>
/// <param name="Missing">Windows that never turned up.</param>
public sealed record WindowLayoutResult(int Placed, int Refused, int Missing)
{
    public bool IsComplete => Refused == 0 && Missing == 0;

    public int Total => Placed + Refused + Missing;
}
