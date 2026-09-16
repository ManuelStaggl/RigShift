namespace RigShift.Core.Profiles;

using RigShift.Core.Abstractions;

/// <summary>
/// Which open window belongs to which saved placement. Pure, because this is where the feature is right or wrong and
/// it must be testable without a desktop.
/// </summary>
public static class WindowLayoutMatching
{
    /// <summary>
    /// Pairs saved placements with open windows. A window is used at most once, so two saved windows of the same
    /// program do not both land on the first one. Matching goes in three passes, each stricter than the next is
    /// forgiving: same program and same title, then same program and a title that still starts the same (a lap
    /// counter or version number in the caption must not lose the window), then simply the next window of that
    /// program.
    /// </summary>
    public static IReadOnlyList<WindowMatch> Match(IReadOnlyList<WindowPlacement> saved, IReadOnlyList<OpenWindow> open)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(open);

        var taken = new HashSet<nint>();
        var matches = new List<WindowMatch>(saved.Count);
        var pending = new List<WindowPlacement>(saved);

        foreach (Func<WindowPlacement, OpenWindow, bool> rule in Rules)
        {
            for (int index = 0; index < pending.Count; index++)
            {
                WindowPlacement placement = pending[index];
                OpenWindow? found = null;
                foreach (OpenWindow window in open)
                {
                    if (!taken.Contains(window.Handle) && rule(placement, window))
                    {
                        found = window;
                        break;
                    }
                }

                if (found is not null)
                {
                    taken.Add(found.Handle);
                    matches.Add(new WindowMatch(placement, found));
                    pending.RemoveAt(index--);
                }
            }
        }

        foreach (WindowPlacement placement in pending)
        {
            matches.Add(new WindowMatch(placement, null));
        }

        return matches;
    }

    private static readonly Func<WindowPlacement, OpenWindow, bool>[] Rules =
    [
        (placement, window) => SameProcess(placement, window)
            && string.Equals(placement.Title ?? string.Empty, window.Title, StringComparison.OrdinalIgnoreCase),
        (placement, window) => SameProcess(placement, window) && SharesTheStartOfTheTitle(placement.Title, window.Title),
        SameProcess,
    ];

    private static bool SameProcess(WindowPlacement placement, OpenWindow window)
        => string.Equals(placement.ProcessName, window.ProcessName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The first word of both titles is the same. Enough to tell "SimHub 9.4.2" from "Dash Studio" without
    /// insisting on a caption that changes every lap.
    /// </summary>
    private static bool SharesTheStartOfTheTitle(string? saved, string open)
    {
        if (string.IsNullOrWhiteSpace(saved) || string.IsNullOrWhiteSpace(open))
        {
            return false;
        }

        ReadOnlySpan<char> first = FirstWord(saved);
        return !first.IsEmpty && first.Equals(FirstWord(open), StringComparison.OrdinalIgnoreCase);
    }

    private static ReadOnlySpan<char> FirstWord(string title)
    {
        ReadOnlySpan<char> trimmed = title.AsSpan().Trim();
        int space = trimmed.IndexOf(' ');
        return space < 0 ? trimmed : trimmed[..space];
    }
}

/// <summary>A saved placement and the window it will be applied to, or <c>null</c> when none is open.</summary>
public sealed record WindowMatch(WindowPlacement Placement, OpenWindow? Window);
