using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

/// <summary>Remembers the active plan and the keep-awake request like the OS would.</summary>
internal sealed class FakePowerController : IPowerController
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    public Guid ActivePlan { get; set; } = Balanced;

    public List<Guid> PlansSet { get; } = [];

    public bool Fail { get; set; }

    public bool IsKeepingAwake { get; private set; }

    public IReadOnlyList<PowerPlan> ListPlans() => [new(Balanced, "Balanced"), new(HighPerformance, "High performance")];

    public Guid GetActivePlan() => Fail ? throw new InvalidOperationException("power API failed") : ActivePlan;

    public void SetActivePlan(Guid planId)
    {
        if (Fail)
        {
            throw new InvalidOperationException("power API failed");
        }

        ActivePlan = planId;
        PlansSet.Add(planId);
    }

    public void SetKeepAwake(bool keepAwake)
    {
        if (Fail)
        {
            throw new InvalidOperationException("power API failed");
        }

        IsKeepingAwake = keepAwake;
    }
}
