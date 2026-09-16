namespace RigShift.Core.Topology;

/// <summary>Time and attempt budget for <see cref="SwitchOrchestrator"/>.</summary>
public sealed record SwitchOptions
{
    /// <summary>
    /// Confirmation timeout when nothing else sets one, and the minimum for switches from a <c>rigshift://</c> link,
    /// which always ask (analysis finding H-02).
    /// </summary>
    public static TimeSpan DefaultConfirmTimeout { get; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long to wait for targets that are attached but not ready (sleeping HDMI monitor, error 31).
    /// Observed on real hardware: the same call succeeded about 20 seconds after the first failure.
    /// </summary>
    public TimeSpan TargetWaitBudget { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// How long to wait for a required display that is not connected at all (switched off, or dropped off the bus in
    /// standby) before the switch is blocked; the user is asked to switch it on (finding HW-16).
    /// </summary>
    public TimeSpan MissingDisplayWaitBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Longest time a native HDR call may take. Switching HDR over HDMI froze the graphics stack of the test PC (finding
    /// HW-12); the switch goes on without waiting for a call that does not return.
    /// </summary>
    public TimeSpan HdrCallTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the displays to settle after an apply before HDR is switched (finding HW-12).</summary>
    public TimeSpan HdrSettleBudget { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Interval for re-querying the topology while waiting.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Hard cap on SetDisplayConfig calls per apply phase (each cycle uses two: stored modes, database modes).</summary>
    public int MaxApplyAttempts { get; init; } = 40;

    /// <summary>How long an app may take to close after its windows were asked to, before it is ended.</summary>
    public TimeSpan AppStopGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Pause between a successful apply and moving lost windows: Windows and the apps rearrange windows themselves
    /// right after a topology change, and a move before that would be undone.
    /// </summary>
    public TimeSpan WindowRescueDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// How long to wait before each attempt at the desktop symbols. Explorer rearranges them itself a moment after the
    /// arrangement changed, so the first attempt can be overwritten again.
    /// </summary>
    public TimeSpan DesktopIconDelay { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often to put the desktop symbols back while Explorer keeps moving them.</summary>
    public int DesktopIconAttempts { get; init; } = 3;

    /// <summary>Interval for checking whether the device the apps wait for is connected.</summary>
    public TimeSpan DevicePollInterval { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>Per-call switches, e.g. from the CLI (<c>--dry-run</c>, <c>--no-confirm</c>).</summary>
public sealed record SwitchRequest
{
    public static SwitchRequest Default { get; } = new();

    /// <summary>Compute and report the plan without touching displays or audio.</summary>
    public bool DryRun { get; init; }

    /// <summary>Skip the keep-or-revert confirmation even if the profile asks for it.</summary>
    public bool SkipConfirmation { get; init; }

    /// <summary>
    /// Windows already restored the profile's displays itself: apply only audio, keep-awake, call ducking and apps, without
    /// touching the displays and without asking (finding HW-15).
    /// </summary>
    public bool KeepDisplays { get; init; }

    /// <summary>
    /// The switch came from a <c>rigshift://</c> link, i.e. possibly from a web page: it always asks for confirmation,
    /// regardless of <see cref="SkipConfirmation"/> and a timeout of 0.
    /// </summary>
    public bool FromLink { get; init; }

    /// <summary>Confirmation timeout for profiles that do not set their own (application setting).</summary>
    public int DefaultConfirmTimeoutSeconds { get; init; } = (int)SwitchOptions.DefaultConfirmTimeout.TotalSeconds;
}
