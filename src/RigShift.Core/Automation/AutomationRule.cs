using System.Text.Json.Serialization;

namespace RigShift.Core.Automation;

/// <summary>What a rule does once its device is gone (docs/PLAN.md, section 6).</summary>
public enum ExitAction
{
    /// <summary>Stay in the rule's profile.</summary>
    Stay,

    /// <summary>Switch back to the profile that was active when the device connected.</summary>
    SwitchBack,

    /// <summary>Switch to <see cref="AutomationRule.ExitProfileId"/>.</summary>
    SwitchTo,
}

/// <summary>
/// "When this USB device connects, switch to that profile." Stored in the application settings. A rule without the
/// <see cref="UsbDeviceId"/> key (written by an unreleased build) is ignored.
/// </summary>
public sealed record AutomationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// The per-rule switch of 1.3.x, read only so that a rule switched off there is dropped on load instead of coming back
    /// on (analysis finding O-07); never written.
    /// </summary>
    [JsonPropertyName("isEnabled")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? LegacyIsEnabled { get; init; }

    /// <summary>USB device as <c>VID_xxxx&amp;PID_xxxx</c>; empty until a device is picked.</summary>
    public string? UsbDeviceId { get; init; }

    /// <summary>Name of the device when it was picked, shown while it is not connected.</summary>
    public string? UsbDeviceName { get; init; }

    public Guid ProfileId { get; init; }

    public ExitAction OnExit { get; init; }

    public Guid? ExitProfileId { get; init; }

    /// <summary>Switch without the keep-or-revert countdown; otherwise the profile or application setting applies.</summary>
    public bool SkipConfirmation { get; init; }

    /// <summary>
    /// Seconds the device must stay gone before the end action runs, so turning a wheelbase off and on again is
    /// no end. <c>set</c>: a rule written without the key keeps the initializer value.
    /// </summary>
    public int ExitDelaySeconds { get; set; } = DefaultExitDelaySeconds;

    public const int DefaultExitDelaySeconds = 10;

    public const int MaxExitDelaySeconds = 600;
}
