using RigShift.Core.Topology;

namespace RigShift.App.Services;

/// <summary>
/// What the confirmation window shows: the arrangement before the switch beside the one that is on screen now
/// (spec F2 phase 2, "before → after"). The names are the profiles' where one matches, otherwise a plain label.
/// </summary>
/// <param name="ProfileId">The profile that was switched to; its hotkey confirms an open countdown.</param>
public sealed record ConfirmationView(
    Guid ProfileId,
    IReadOnlyList<TopologyDisplay> BeforeTopology,
    string BeforeName,
    IReadOnlyList<TopologyDisplay> AfterTopology,
    string AfterName);
