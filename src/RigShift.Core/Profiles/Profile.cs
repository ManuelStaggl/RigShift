namespace RigShift.Core.Profiles;

/// <summary>
/// A named target state for the machine: which displays are active and where,
/// which audio endpoints are default, plus optional actions (v1.x).
/// Profiles are pure data and never contain volatile identifiers (adapter LUIDs, target IDs).
/// </summary>
public sealed record Profile
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Optional symbol key, one of <see cref="ProfileIcons.All"/>; unknown keys show the RigShift symbol.</summary>
    public string? Icon { get; init; }

    public required IReadOnlyList<DisplayAssignment> Displays { get; init; }

    public AudioAssignment Audio { get; init; } = new();

    /// <summary>
    /// Seconds the user has to confirm a switch before the previous profile is restored. 0 disables the safety net
    /// for this profile; <c>null</c> uses the application-wide setting.
    /// </summary>
    public int? ConfirmTimeoutSeconds { get; init; }
}
