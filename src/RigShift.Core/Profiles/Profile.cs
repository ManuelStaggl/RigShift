using System.Text.Json.Serialization;

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

    /// <summary><c>set</c>: an <c>init</c> initializer is skipped when the key is missing (docs/PLAN.md, stumbling blocks).</summary>
    public AudioAssignment Audio { get; set; } = new();

    /// <summary>
    /// Switch to this profile without the keep-or-revert question. The countdown seconds come from the application
    /// setting only (analysis decision O-01).
    /// </summary>
    public bool SwitchWithoutAsking { get; init; }

    /// <summary>
    /// Per-profile confirmation seconds from files before 1.4, read only for migration (0 meant "without asking"), see
    /// <see cref="WithMigratedConfirmation"/>. Not written once it is <c>null</c>.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ConfirmTimeoutSeconds { get; init; }

    /// <summary>System-wide key combination that switches to this profile while the tray app runs; <c>null</c> for none.</summary>
    public Hotkey? Hotkey { get; init; }

    /// <summary>
    /// Programs to start or end, in order, after the switch is confirmed. <c>set</c> instead of <c>init</c>: the JSON
    /// source generator skips the initializer of an <c>init</c> property when the key is missing (profiles before 1.3).
    /// </summary>
    public IReadOnlyList<AppAction> Apps { get; set; } = [];

    /// <summary>No standby, screen saver or display timeout while this profile is active (docs/PLAN.md, section 6, item 8).</summary>
    public bool KeepAwake { get; init; }

    /// <summary>
    /// USB device (<c>VID_xxxx&amp;PID_xxxx</c>) the apps wait for before they start, e.g. the wheelbase; <c>null</c> to start
    /// them right away (docs/PLAN.md, section 6, "Neu für 1.3", item 2).
    /// </summary>
    public string? AppsWaitForUsbDeviceId { get; init; }

    /// <summary>Name of <see cref="AppsWaitForUsbDeviceId"/> when it was picked, for messages while it is not connected.</summary>
    public string? AppsWaitForUsbDeviceName { get; init; }

    /// <summary>
    /// Wait time written by 1.3. No longer read – the wait is always <see cref="AppsDeviceWaitSeconds"/> (analysis decision
    /// O-04); kept so the value survives in the file. <c>set</c>: an <c>init</c> initializer is skipped when the key is missing.
    /// </summary>
    public int AppsWaitSeconds { get; set; } = AppsDeviceWaitSeconds;

    /// <summary>Don't lower other sounds during calls while this profile is active (Windows communications setting).</summary>
    public bool DisableCommunicationsDucking { get; init; }

    /// <summary>Longer names push the badges and buttons off the profile card (analysis finding I-06).</summary>
    public const int MaxNameLength = 60;

    /// <summary>Longest wait for <see cref="AppsWaitForUsbDeviceId"/> before the apps start anyway.</summary>
    public const int AppsDeviceWaitSeconds = 30;

    /// <summary>
    /// Moves a per-profile confirmation time from before 1.4 to <see cref="SwitchWithoutAsking"/>: 0 becomes "without asking",
    /// any other value asks with the application setting's seconds.
    /// </summary>
    public Profile WithMigratedConfirmation() => ConfirmTimeoutSeconds is not { } seconds
        ? this
        : this with { SwitchWithoutAsking = SwitchWithoutAsking || seconds <= 0, ConfirmTimeoutSeconds = null };
}
