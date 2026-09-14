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
    /// Seconds the user has to confirm a switch before the previous profile is restored. 0 disables the safety net
    /// for this profile; <c>null</c> uses the application-wide setting.
    /// </summary>
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
    /// Longest wait for <see cref="AppsWaitForUsbDeviceId"/>. <c>set</c>: an <c>init</c> initializer is skipped when the key
    /// is missing. Use <see cref="ClampAppsWaitSeconds"/> when reading it.
    /// </summary>
    public int AppsWaitSeconds { get; set; } = DefaultAppsWaitSeconds;

    /// <summary>Don't lower other sounds during calls while this profile is active (Windows communications setting).</summary>
    public bool DisableCommunicationsDucking { get; init; }

    /// <summary>Longer names push the badges and buttons off the profile card (analysis finding I-06).</summary>
    public const int MaxNameLength = 60;

    public const int DefaultAppsWaitSeconds = 30;

    public const int MinAppsWaitSeconds = 5;

    public const int MaxAppsWaitSeconds = 300;

    public static int ClampAppsWaitSeconds(int seconds) => Math.Clamp(seconds, MinAppsWaitSeconds, MaxAppsWaitSeconds);
}
