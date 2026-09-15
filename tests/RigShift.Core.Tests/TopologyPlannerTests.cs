using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class TopologyPlannerTests
{
    private readonly TopologyPlanner _planner = new(new TopologyPlannerOptions());

    [Fact]
    public void Plan_MatchesByDevicePath()
    {
        TopologyPlan plan = _planner.Plan(Rig(), DeskActive());

        plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Ultrawide, Tablet]);
        plan.Missing.ShouldBeEmpty();
        plan.Warnings.ShouldBeEmpty();
        plan.IsBlocked.ShouldBeFalse();
    }

    [Fact]
    public void Plan_MatchesCaseInsensitively()
    {
        DisplayIdentity upper = Ultrawide with
        {
            TargetDevicePath = Ultrawide.TargetDevicePath.ToUpperInvariant(),
            AdapterDevicePath = Ultrawide.AdapterDevicePath.ToLowerInvariant(),
        };

        TopologyPlan plan = _planner.Plan(Profile("Rig", [UltrawideMode]), Snapshot(Attached(upper)));

        plan.Resolved.Count.ShouldBe(1);
        plan.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_MatchesByEdid_WhenPortChanged_AndWarns()
    {
        DisplayIdentity movedPort = Ultrawide with { TargetDevicePath = @"\\?\DISPLAY#SAM0001#OTHERPORT&9" };

        TopologyPlan plan = _planner.Plan(Profile("Rig", [UltrawideMode]), Snapshot(Attached(movedPort)));

        plan.Resolved.Single().Target.Identity.ShouldBe(movedPort);
        plan.Warnings.Single().Kind.ShouldBe(PlanWarningKind.MatchedByEdidFallback);
    }

    [Fact]
    public void Plan_EdidFallback_NeverStealsADisplayMatchedByPath()
    {
        DisplayIdentity rightMoved = DeskRight with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#OTHERPORT&9" };
        // Right is listed first so a naive single pass would grab the left monitor by EDID.
        Profile desk = Profile("Desk", [DeskModes[2], DeskModes[1]]);

        TopologyPlan plan = _planner.Plan(desk, Snapshot(Attached(DeskLeft), Attached(rightMoved)));

        plan.Resolved.Single(r => r.Assignment.Identity == DeskRight).Target.Identity.ShouldBe(rightMoved);
        plan.Resolved.Single(r => r.Assignment.Identity == DeskLeft).Target.Identity.ShouldBe(DeskLeft);
    }

    [Fact]
    public void Plan_AmbiguousEdid_IsNotMatched()
    {
        DisplayIdentity twinA = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&1" };
        DisplayIdentity twinB = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&2" };

        TopologyPlan plan = _planner.Plan(Profile("Desk", [DeskModes[1]]), Snapshot(Attached(twinA), Attached(twinB)));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Single().Reason.ShouldBe(MissingReason.NotAttached);
    }

    [Fact]
    public void Plan_TwinMonitors_OneMissingOneMoved_DoesNotGuess()
    {
        // Two identical desk monitors: the left one is unplugged, the right one moved to another port.
        DisplayIdentity rightMoved = DeskRight with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#OTHERPORT&9" };
        Profile desk = Profile("Desk", [DeskModes[1], DeskModes[2]]);

        TopologyPlan plan = _planner.Plan(desk, Snapshot(Attached(rightMoved)));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Select(m => m.Reason).ShouldBe([MissingReason.NotAttached, MissingReason.NotAttached]);
        plan.IsBlocked.ShouldBeTrue();
        plan.Warnings.ShouldContain(w => w.Kind == PlanWarningKind.AmbiguousTwin);
    }

    [Fact]
    public void Plan_SameMonitorOnStaleAndLiveTarget_PrefersAvailable()
    {
        TopologyPlan plan = _planner.Plan(Profile("Rig", [UltrawideMode]), Snapshot(Attached(Ultrawide, available: false), Attached(Ultrawide)));

        plan.Resolved.Single().Target.IsAvailable.ShouldBeTrue();
        plan.Missing.ShouldBeEmpty();
        plan.Warnings.ShouldBeEmpty();
    }

    [Fact]
    public void Plan_OptionalDisplayMissing_ShouldRetryLater()
    {
        TopologyPlan plan = _planner.Plan(Rig(), DeskActive(tabletAttached: false));

        plan.IsBlocked.ShouldBeFalse();
        plan.ShouldRetryLater.ShouldBeTrue();
        plan.Resolved.Single().Target.Identity.ShouldBe(Ultrawide);
        plan.Missing.Single().ShouldSatisfyAllConditions(
            m => m.Assignment.Identity.ShouldBe(Tablet),
            m => m.Reason.ShouldBe(MissingReason.NotAttached));
    }

    [Fact]
    public void Plan_RequiredDisplayMissing_IsBlocked()
    {
        TopologyPlan plan = _planner.Plan(Rig(), Snapshot(Attached(Desk4K), Attached(Tablet)));

        plan.IsBlocked.ShouldBeTrue();
        plan.ShouldRetryLater.ShouldBeFalse();
        plan.Missing.Single().Reason.ShouldBe(MissingReason.NotAttached);
    }

    [Fact]
    public void Plan_SleepingRequiredDisplay_IsAttachedButUnavailable()
    {
        TopologyPlan plan = _planner.Plan(Rig(), DeskActive(ultrawideAvailable: false));

        plan.IsBlocked.ShouldBeTrue();
        plan.Missing.Single().ShouldSatisfyAllConditions(
            m => m.Assignment.Identity.ShouldBe(Ultrawide),
            m => m.Reason.ShouldBe(MissingReason.AttachedButUnavailable));
    }

    [Fact]
    public void Plan_HeadBudgetExceeded_WarnsButDoesNotBlock()
    {
        // 5120x1440@240 (2) + 4K@165 (2) + 2x 1080p@100 (1+1) = 6 heads on one adapter, budget 4.
        Profile everything = Profile("Everything", [UltrawideMode, .. DeskModes]);

        TopologyPlan plan = _planner.Plan(everything, DeskActive());

        plan.IsBlocked.ShouldBeFalse();
        PlanWarning warning = plan.Warnings.Single();
        warning.Kind.ShouldBe(PlanWarningKind.HeadBudgetExceeded);
        warning.Message.ShouldContain("6 display heads");
        warning.Message.ShouldContain("budget is 4");
    }

    [Fact]
    public void Plan_DeskWithinHeadBudget_HasNoWarning()
    {
        // 4K@165 (2) + 2x 1080p@100 (1+1) = exactly 4.
        TopologyPlan plan = _planner.Plan(Profile("Desk", DeskModes), DeskActive());

        plan.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"\\?\PCI#VEN_1002&DEV_73BF#TEST#1")]
    [InlineData(@"\\?\PCI#VEN_8086&DEV_A780#TEST#1")]
    public void Plan_HeadBudget_AmdAndIntelAreNotChecked_UnlessConfigured(string adapter)
    {
        // The same six-head layout as above, on a card whose limits the NVIDIA rule does not describe.
        DisplayAssignment[] modes = [UltrawideMode, .. DeskModes];
        modes = [.. modes.Select(m => m with { Identity = m.Identity with { AdapterDevicePath = adapter } })];
        DisplaySnapshot snapshot = Snapshot([.. modes.Select(m => Attached(m.Identity, activeMode: m))]);

        _planner.Plan(Profile("Everything", modes), snapshot).Warnings.ShouldBeEmpty();

        var configured = new TopologyPlanner(new TopologyPlannerOptions { HeadBudgetByAdapter = new Dictionary<string, int> { [adapter] = 4 } });
        configured.Plan(Profile("Everything", modes), snapshot).Warnings.ShouldHaveSingleItem().Kind.ShouldBe(PlanWarningKind.HeadBudgetExceeded);
    }

    [Theory]
    [InlineData(@"\\?\PCI#VEN_10DE&DEV_2702&SUBSYS_1#4&1", GpuVendor.Nvidia)]
    [InlineData(@"\\?\pci#ven_1002&dev_73bf#x", GpuVendor.Amd)]
    [InlineData(@"\\?\PCI#VEN_8086&DEV_A780#x", GpuVendor.Intel)]
    [InlineData(@"\\?\SWD#SPACEDESK#TEST#1", GpuVendor.Unknown)]
    [InlineData(null, GpuVendor.Unknown)]
    public void GpuVendors_FromAdapterPath(string? adapter, GpuVendor expected) =>
        GpuVendors.Of(adapter).ShouldBe(expected);

    [Fact]
    public void Plan_HeadBudget_CountsPerAdapter_AndHonoursOverride()
    {
        var planner = new TopologyPlanner(new TopologyPlannerOptions
        {
            HeadBudgetByAdapter = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [Gpu] = 6 },
        });
        Profile everything = Profile("Everything", [UltrawideMode, TabletMode, .. DeskModes]);

        TopologyPlan plan = planner.Plan(everything, DeskActive());

        plan.Warnings.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(3840, 2160, 165, 2)]
    [InlineData(5120, 1440, 240, 2)]
    [InlineData(3840, 2160, 60, 1)]
    [InlineData(1920, 1080, 100, 1)]
    public void EstimateHeads_UsesPixelRateThreshold(int width, int height, uint hertz, int expected)
    {
        _planner.EstimateHeads(Mode(Desk4K, width, height, hertz)).ShouldBe(expected);
    }

    [Fact]
    public void Plan_WithoutPrimary_Warns()
    {
        Profile profile = Profile("Side screens", [DeskModes[1], DeskModes[2]]);

        TopologyPlan plan = _planner.Plan(profile, DeskActive());

        plan.Warnings.Single().Kind.ShouldBe(PlanWarningKind.NoPrimary);
    }
}
