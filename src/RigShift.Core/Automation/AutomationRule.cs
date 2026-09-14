namespace RigShift.Core.Automation;

/// <summary>What a rule does once its game has closed or its device is gone (docs/PLAN.md, section 6).</summary>
public enum ExitAction
{
    /// <summary>Stay in the rule's profile.</summary>
    Stay,

    /// <summary>Switch back to the profile that was active when the game started or the device connected.</summary>
    SwitchBack,

    /// <summary>Switch to <see cref="AutomationRule.ExitProfileId"/>.</summary>
    SwitchTo,
}

/// <summary>
/// "When this game starts (or this USB device connects), switch to that profile." Stored in the application settings.
/// A rule has exactly one trigger: <see cref="UsbDeviceId"/> when set, otherwise the game.
/// </summary>
public sealed record AutomationRule
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// A regular setter on purpose: the JSON source generator keeps the initializer only for settable properties, and a
    /// rule written without the key must stay on.
    /// </summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>Built-in game template, or <c>null</c> for a custom program in <see cref="ExecutablePath"/>.</summary>
    public string? TemplateId { get; init; }

    /// <summary>Program of a custom game; only its file name is matched, like Task Manager shows processes.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>USB device as <c>VID_xxxx&amp;PID_xxxx</c>; set, the rule watches this device instead of a game.</summary>
    public string? UsbDeviceId { get; init; }

    /// <summary>Name of the device when it was picked, shown while it is not connected.</summary>
    public string? UsbDeviceName { get; init; }

    public Guid ProfileId { get; init; }

    public ExitAction OnExit { get; init; }

    public Guid? ExitProfileId { get; init; }

    /// <summary>Switch without the keep-or-revert countdown; otherwise the profile or application setting applies.</summary>
    public bool SkipConfirmation { get; init; }

    /// <summary>
    /// Seconds the game or device must stay gone before the end action runs, so turning a wheelbase off and on again is
    /// no end. <c>set</c>: a rule written without the key keeps the initializer value.
    /// </summary>
    public int ExitDelaySeconds { get; set; } = DefaultExitDelaySeconds;

    public const int DefaultExitDelaySeconds = 10;

    public const int MaxExitDelaySeconds = 600;
}
