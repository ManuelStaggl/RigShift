using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

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

    /// <summary>
    /// What happened to NVIDIA Surround. Unlike audio this one can stop a switch: a grid that was not built means the
    /// arrangement the profile describes does not exist.
    /// </summary>
    public SurroundOutcome Surround { get; init; } = SurroundOutcome.NotConfigured;

    /// <summary>
    /// Apps are judged separately too; they only run after a confirmed switch. <see cref="AppsOutcome.Pending"/> when they
    /// run after the result – their outcome then comes with <see cref="AppsCompletion"/> (analysis finding B-03).
    /// </summary>
    public AppsOutcome Apps { get; init; } = AppsOutcome.NotConfigured;

    /// <summary>Completes with the final apps outcome; never faults. Already complete when no apps run.</summary>
    public Task<AppsOutcome> AppsCompletion { get; init; } = NoApps;

    /// <summary>Whether every display got the HDR state the profile sets; only in the log before 4.0 (v4 finding K-15).</summary>
    public HdrOutcome Hdr { get; init; } = HdrOutcome.NotConfigured;

    /// <summary>
    /// What became of the profile's desktop symbols (K-15). <see cref="DesktopIconOutcome.Pending"/> when they go back
    /// after the result – the outcome then comes with <see cref="TidyCompletion"/> (v4 finding K-04).
    /// </summary>
    public DesktopIconOutcome DesktopIcons { get; init; } = DesktopIconOutcome.NotConfigured;

    /// <summary>
    /// Completes once the tidy-up after the result ended – windows moved off displays that are off, the profile's desktop
    /// symbols put back – with what became of the symbols. Never faults. Already complete when there is nothing to tidy.
    /// </summary>
    public Task<DesktopIconOutcome> TidyCompletion { get; init; } = NothingToTidy;

    internal static Task<AppsOutcome> NoApps { get; } = Task.FromResult(AppsOutcome.NotConfigured);

    internal static Task<DesktopIconOutcome> NothingToTidy { get; } = Task.FromResult(DesktopIconOutcome.NotConfigured);
}

public enum HdrOutcome
{
    /// <summary>The profile sets no HDR state, or the switch did not get that far.</summary>
    NotConfigured,

    Applied,

    /// <summary>At least one display kept its HDR state: it cannot do HDR, did not settle, or the call failed; see the log.</summary>
    Incomplete,
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

    /// <summary>A call into the graphics driver did not return; nothing could be restored (v4 finding K-07).</summary>
    DriverHung,
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

    /// <summary>The apps still run (or wait for their device) after the switch result; the outcome follows.</summary>
    Pending,

    /// <summary>A newer switch or the app exiting cancelled the apps before they were done.</summary>
    Cancelled,
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
    /// <summary>
    /// Topology and audio applied, user confirmed (or no confirmation required). Optional displays that are not there count
    /// as applied (finding HW-03); they are in <see cref="TopologyPlan.Missing"/> and a follow-up pass picks them up.
    /// </summary>
    Applied,

    /// <summary>Applied, but a display stayed dark with the database modes; a follow-up pass may pick it up.</summary>
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
