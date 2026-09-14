using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for power plans and the keep-awake request (docs/PLAN.md, section 6, items 8 + 9).</summary>
public interface IPowerController
{
    /// <summary>The power plans of this machine with their display names.</summary>
    IReadOnlyList<PowerPlan> ListPlans();

    Guid GetActivePlan();

    void SetActivePlan(Guid planId);

    /// <summary>True while RigShift holds a request that keeps the system and the displays on.</summary>
    bool IsKeepingAwake { get; }

    /// <summary>
    /// Holds or releases the request against standby, screen saver and display timeout. Windows drops it on its own
    /// when the process ends, so a crash never leaves the machine awake.
    /// </summary>
    void SetKeepAwake(bool keepAwake);
}
