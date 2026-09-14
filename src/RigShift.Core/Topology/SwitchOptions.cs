namespace RigShift.Core.Topology;

/// <summary>Time and attempt budget for <see cref="SwitchOrchestrator"/>.</summary>
public sealed record SwitchOptions
{
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
}

/// <summary>Per-call switches, e.g. from the CLI (<c>--dry-run</c>, <c>--no-confirm</c>).</summary>
public sealed record SwitchRequest
{
    public static SwitchRequest Default { get; } = new();

    /// <summary>Compute and report the plan without touching displays or audio.</summary>
    public bool DryRun { get; init; }

    /// <summary>Skip the keep-or-revert confirmation even if the profile asks for it.</summary>
    public bool SkipConfirmation { get; init; }

    /// <summary>Confirmation timeout for profiles that do not set their own (application setting).</summary>
    public int DefaultConfirmTimeoutSeconds { get; init; } = 15;
}
