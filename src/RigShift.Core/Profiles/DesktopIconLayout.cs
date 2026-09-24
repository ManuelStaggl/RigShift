namespace RigShift.Core.Profiles;

/// <summary>
/// Where the desktop symbols sit. Windows rearranges them whenever the arrangement changes – a different main display
/// or a different resolution is enough – and it keeps one layout, not one per profile. Sorting them by hand after
/// every switch is the second half of the job RigShift already does for windows (see <see cref="WindowLayout"/>).
///
/// The positions are the ones the shell itself reports, in the coordinates of the desktop view, and they are reliable
/// for the same reason the window positions are: RigShift applies the display arrangement first and puts the symbols
/// back afterwards, so they land in the arrangement they were captured in.
/// </summary>
public sealed record DesktopIconLayout
{
    /// <summary>The symbols to put back. Empty means the profile has none captured yet.</summary>
    public IReadOnlyList<DesktopIcon> Icons { get; init; } = [];

    /// <summary>When the layout was captured, so the editor can say how old it is.</summary>
    public DateTimeOffset CapturedAt { get; init; }

    public bool IsEmpty => Icons.Count == 0;
}

/// <summary>One symbol's place on the desktop.</summary>
public sealed record DesktopIcon
{
    /// <summary>
    /// What the symbol is: the full path for a file or shortcut, <c>::{GUID}</c> for This PC, the Recycle Bin and the
    /// other shell folders. The shell's own parsing name, so it survives a reboot – unlike the index in the view, which
    /// changes as soon as anything is added, removed or renamed.
    /// </summary>
    public required string Item { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }
}

/// <summary>What became of a restore.</summary>
public enum DesktopIconOutcome
{
    /// <summary>The profile has no captured layout.</summary>
    NotConfigured,

    /// <summary>The symbols were put back.</summary>
    Restored,

    /// <summary>
    /// Windows arranges the symbols itself ("Auto arrange icons"), so it overrides every position we set. Nothing to
    /// fix in RigShift – the setting sits in the desktop's context menu.
    /// </summary>
    AutoArrange,

    /// <summary>The desktop view was not reachable; the log says why.</summary>
    Unavailable,

    /// <summary>Only in a switch result: the symbols go back after it, the outcome follows (v4 finding K-04).</summary>
    Pending,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Placed">How many symbols were put back.</param>
/// <param name="Missing">How many of the captured symbols are no longer on the desktop.</param>
public readonly record struct DesktopIconResult(DesktopIconOutcome Outcome, int Placed, int Missing)
{
    public static readonly DesktopIconResult NotConfigured = new(DesktopIconOutcome.NotConfigured, 0, 0);

    public static DesktopIconResult Unavailable => new(DesktopIconOutcome.Unavailable, 0, 0);
}
