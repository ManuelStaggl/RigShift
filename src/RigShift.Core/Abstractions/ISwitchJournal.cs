using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Remembers the topology from before a switch for as long as that switch runs. The rollback in
/// <see cref="Topology.SwitchOrchestrator"/> lives in the process and dies with it: a crash, a forced end or a power
/// loss between the apply and the confirmation leaves the machine in the new layout with nobody left to undo it – and
/// that is exactly the moment the screen may be dark. The journal survives that, so the next start can offer the way
/// back.
/// </summary>
public interface ISwitchJournal
{
    /// <summary>Records an unfinished switch, replacing any earlier entry.</summary>
    Task BeginAsync(InterruptedSwitch entry, CancellationToken cancellationToken);

    /// <summary>Forgets the recorded switch; does nothing when there is none.</summary>
    Task ClearAsync(CancellationToken cancellationToken);

    /// <summary>The recorded switch, or <c>null</c> when none is recorded or the record is unreadable.</summary>
    Task<InterruptedSwitch?> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>A switch that was started but never finished, with the topology it started from.</summary>
public sealed record InterruptedSwitch
{
    /// <summary>The topology that was active before the switch, as a profile the orchestrator can apply again.</summary>
    public required Profile Previous { get; init; }

    /// <summary>Name of the profile the interrupted switch was heading for, for the question asked at the next start.</summary>
    public required string TargetProfileName { get; init; }

    /// <summary>When the interrupted switch started, so a long-forgotten record can be judged.</summary>
    public required DateTimeOffset StartedUtc { get; init; }
}
