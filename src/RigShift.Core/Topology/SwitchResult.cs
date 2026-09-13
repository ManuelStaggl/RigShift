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

    /// <summary>Audio is judged separately: an audio problem never fails the display switch.</summary>
    public AudioOutcome Audio { get; init; } = AudioOutcome.NotConfigured;
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
