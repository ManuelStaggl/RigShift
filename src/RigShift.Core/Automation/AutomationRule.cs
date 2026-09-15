using System.Text.Json.Serialization;

namespace RigShift.Core.Automation;

/// <summary>What a rule does once its device is gone.</summary>
public enum ExitAction
{
    /// <summary>Stay in the rule's profile.</summary>
    Stay,

    /// <summary>Switch back to the profile that was active when the device connected.</summary>
    SwitchBack,

    /// <summary>Switch to <see cref="AutomationRule.ExitProfileId"/>.</summary>
    SwitchTo,
}

/// <summary>A USB device a rule watches.</summary>
public sealed record RuleDevice
{
    /// <summary>The device as <c>VID_xxxx&amp;PID_xxxx</c>.</summary>
    public string? Id { get; init; }

    /// <summary>Windows' name of the device when it was picked, shown while it is not connected.</summary>
    public string? Name { get; init; }
}

/// <summary>
/// "When these USB devices are connected, switch to that profile." Stored in the application settings. A rule without
/// devices and without the 1.3 key <c>usbDeviceId</c> (written by an unreleased build) is ignored.
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

    /// <summary>
    /// The devices that must all be connected for the rule to start; the end action runs once one of them has been gone
    /// for the delay (user decision U-02). Empty until a device is picked.
    /// </summary>
    public IReadOnlyList<RuleDevice>? Devices { get; init; }

    /// <summary>The single device of 1.3.x, read only and moved to <see cref="Devices"/> on load; never written.</summary>
    [JsonPropertyName("usbDeviceId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUsbDeviceId { get; init; }

    /// <summary>Name of <see cref="LegacyUsbDeviceId"/>, moved with it.</summary>
    [JsonPropertyName("usbDeviceName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyUsbDeviceName { get; init; }

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

    /// <summary>The rule in the current shape: the device of 1.3.x becomes the only entry of <see cref="Devices"/>.</summary>
    public AutomationRule Migrated() => LegacyUsbDeviceId is null && LegacyUsbDeviceName is null
        ? this
        : this with
        {
            Devices = Devices ?? (UsbDeviceIds.Normalize(LegacyUsbDeviceId) is { } id ? [new RuleDevice { Id = id, Name = LegacyUsbDeviceName }] : []),
            LegacyUsbDeviceId = null,
            LegacyUsbDeviceName = null,
        };
}
