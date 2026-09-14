using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// Result of matching a <see cref="Profile"/> against a <see cref="DisplaySnapshot"/>:
/// what will be applied, what is skipped, and whether the switch is expected to work at all.
/// This is the object shown to the user before and after a switch instead of a raw error code.
/// </summary>
public sealed record TopologyPlan
{
    public required Profile Profile { get; init; }

    /// <summary>Displays that were found and will be activated.</summary>
    public required IReadOnlyList<PlannedDisplay> Resolved { get; init; }

    /// <summary>Profile displays that are not attached or not yet available.</summary>
    public required IReadOnlyList<MissingDisplay> Missing { get; init; }

    public required IReadOnlyList<PlanWarning> Warnings { get; init; }

    /// <summary>True if at least one non-optional display is missing.</summary>
    public bool IsBlocked => Missing.Any(m => !m.Assignment.IsOptional);

    /// <summary>True if every missing display is optional and may show up later (spacedesk, sleeping HDMI monitor).</summary>
    public bool ShouldRetryLater => Missing.Count > 0 && !IsBlocked;
}

public sealed record PlannedDisplay(DisplayAssignment Assignment, AttachedDisplay Target);

public sealed record MissingDisplay(DisplayAssignment Assignment, MissingReason Reason);

public enum MissingReason
{
    NotAttached,
    AttachedButUnavailable,
}

public sealed record PlanWarning(PlanWarningKind Kind, string Message);

public enum PlanWarningKind
{
    /// <summary>Requested pixel clock likely exceeds the GPU's display-head budget (NVIDIA DSC: 4K@165 or 5120x1440@240 each take two heads).</summary>
    HeadBudgetExceeded,

    /// <summary>The display was matched by EDID instead of device path (port or cable changed).</summary>
    MatchedByEdidFallback,

    /// <summary>No display in the plan is marked primary; the OS will pick one.</summary>
    NoPrimary,

    /// <summary>
    /// Identical monitors (same EDID) in the profile lost their ports; the model alone cannot tell which is which, so none
    /// is matched by EDID (analysis finding B-10).
    /// </summary>
    AmbiguousTwin,
}
