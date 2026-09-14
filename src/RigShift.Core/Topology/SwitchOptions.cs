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
    /// The switch came from a <c>rigshift://</c> link, i.e. possibly from a web page: it always asks for confirmation,
    /// regardless of <see cref="SkipConfirmation"/> and a timeout of 0.
    /// </summary>
    public bool FromLink { get; init; }

    /// <summary>Confirmation timeout for profiles that do not set their own (application setting).</summary>
    public int DefaultConfirmTimeoutSeconds { get; init; } = (int)SwitchOptions.DefaultConfirmTimeout.TotalSeconds;
}
