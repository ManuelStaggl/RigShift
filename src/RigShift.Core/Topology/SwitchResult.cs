namespace RigShift.Core.Topology;

/// <summary>Outcome of one profile switch, surfaced to the UI (toast/status) and to the CLI exit code.</summary>
public sealed record SwitchResult
{
    public required SwitchOutcome Outcome { get; init; }

    public required TopologyPlan Plan { get; init; }

    public int Attempts { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>OS error code of the last failed attempt, if any (e.g. 31 = ERROR_GEN_FAILURE).</summary>
    public int? LastNativeError { get; init; }

    public string? Message { get; init; }

    /// <summary>What happened besides the outcome, e.g. whether the previous displays came back after a failure.</summary>
    public SwitchNote Note { get; init; }

    /// <summary>Audio is judged separately: an audio problem never fails the display switch.</summary>
    public AudioOutcome Audio { get; init; } = AudioOutcome.NotConfigured;

    /// <summary>Apps are judged separately too; they only run after a confirmed switch.</summary>
    public AppsOutcome Apps { get; init; } = AppsOutcome.NotConfigured;
}

/// <summary>
/// The state the displays are left in when that is not obvious from the outcome – after a failure the user needs to
/// know whether the old picture is back (analysis finding B-05).
/// </summary>
public enum SwitchNote
{
    None,

    /// <summary>A failed switch or a rejected one: the previous displays were applied again.</summary>
    RestoredPrevious,

    /// <summary>The previous displays could not be applied again; some may stay dark.</summary>
    RestoreFailed,

    /// <summary>The stored modes did not work; Windows picked the modes from its own database.</summary>
    ModesFromDatabase,
}

public enum AppsOutcome
{
    /// <summary>The profile has no apps, or they were not reached (rollback, failure, dry run).</summary>
    NotConfigured,

    Applied,

    /// <summary>At least one app could not be started or ended; see the log.</summary>
    Incomplete,

    /// <summary>
    /// The USB device the apps wait for did not show up in time; the apps were started anyway. Wins over
    /// <see cref="Incomplete"/>, because a missing device is the likely cause of an app failing then.
    /// </summary>
    DeviceMissing,
}

public enum AudioOutcome
{
    /// <summary>The profile assigns no audio devices, or audio was not reached.</summary>
    NotConfigured,

    Applied,

    /// <summary>At least one device was inactive or could not be set; see the log.</summary>
    Incomplete,
}

public enum SwitchOutcome
{
    /// <summary>Topology and audio applied, user confirmed (or no confirmation required).</summary>
    Applied,

    /// <summary>Applied with optional displays skipped; a follow-up pass will pick them up.</summary>
    AppliedPartially,

    /// <summary>Applied, but the user did not confirm within the timeout – previous profile restored.</summary>
    RolledBack,

    /// <summary>Plan was blocked before touching the display (missing required display, head budget).</summary>
    Blocked,

    /// <summary>All attempts failed; display left as it was.</summary>
    Failed,

    /// <summary>Dry run: plan computed, nothing applied.</summary>
    DryRun,
}
