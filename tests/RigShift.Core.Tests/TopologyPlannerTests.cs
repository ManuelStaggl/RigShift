using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class TopologyPlannerTests
{
    private const string OldCard = @"\\?\PCI#VEN_10DE&DEV_0000#OLDSLOT#1";
    private const string NewCard = @"\\?\PCI#VEN_10DE&DEV_0000#NEWSLOT#1";
    private const ushort TwinMaker = 0x630E;

    private readonly TopologyPlanner _planner = new(new TopologyPlannerOptions());

    /// <summary>An identical desk monitor as Windows names it: model, the card's instance part, then the port.</summary>
    private static DisplayIdentity Twin(string adapter, string cardInstance, int port, string? serial = null) =>
        Identity(adapter, $@"\\?\DISPLAY#XEC2389#{cardInstance}&0&UID{port}#{{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}}", TwinMaker, 0x2389, "CM27X3")
            with { EdidSerialHash = serial };

    private static Profile TwinDesk(DisplayIdentity left, DisplayIdentity right) =>
        Profile("Desk", [Mode(left, 2560, 1440, 144, primary: true), Mode(right, 2560, 1440, 144, x: 2560)]);

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
    public void Plan_MatchesByName_WhenPathAndEdidBothChanged_AndWarns()
    {
        // HW: the Odyssey G93SC reports one hardware ID over HDMI and another over DisplayPort, and its profile entry
        // was written without an EDID at all. Neither the path nor the EDID finds it after the cable swap.
        DisplayIdentity onDisplayPort = Ultrawide with
        {
            TargetDevicePath = @"\?\DISPLAY#SAM0002#OTHERPORT&7",
            EdidManufacturerId = 0x4C2D,
            EdidProductCodeId = 0x0002,
        };
        Profile profile = Profile("Rig", [UltrawideMode with { Identity = Ultrawide with { EdidManufacturerId = 0, EdidProductCodeId = 0 } }]);

        TopologyPlan plan = _planner.Plan(profile, Snapshot(Attached(onDisplayPort)));

        plan.Resolved.Single().Target.Identity.ShouldBe(onDisplayPort);
        plan.Warnings.Single().Kind.ShouldBe(PlanWarningKind.MatchedByNameFallback);
    }

    [Fact]
    public void Plan_DoesNotMatchByName_WhenTwoDisplaysShareIt()
    {
        // Two identical monitors: the name says nothing, so this stays as ambiguous as the EDID pass leaves it.
        DisplayIdentity twin = Desk4K with { FriendlyName = "Ultrawide 49", TargetDevicePath = @"\?\DISPLAY#AUS0002#OTHER&3" };
        Profile profile = Profile("Rig", [
            UltrawideMode with { Identity = Ultrawide with { EdidManufacturerId = 0, EdidProductCodeId = 0, TargetDevicePath = @"\?\DISPLAY#GONE#1" } },
            UltrawideMode with { Identity = Ultrawide with { EdidManufacturerId = 0, EdidProductCodeId = 0, TargetDevicePath = @"\?\DISPLAY#GONE#2" } },
        ]);

        TopologyPlan plan = _planner.Plan(profile, Snapshot(Attached(twin)));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Count.ShouldBe(2);
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
    public void Plan_AmbiguousEdid_IsNotMatched_AndSaysWhy()
    {
        // Both candidates are attached: asking the user to switch the monitor on would be wrong (K-03).
        DisplayIdentity twinA = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&1" };
        DisplayIdentity twinB = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&2" };

        TopologyPlan plan = _planner.Plan(Profile("Desk", [DeskModes[1]]), Snapshot(Attached(twinA), Attached(twinB)));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Single().Reason.ShouldBe(MissingReason.Ambiguous);
        plan.IsAmbiguous.ShouldBeTrue();
    }

    [Fact]
    public void Plan_TwinsOnANewCard_AreToldApartByTheirSerialNumbers()
    {
        // K-03: after a new graphics card every path is new. The right twin even sits on the left one's old port number,
        // so only the serial number puts each back in its place.
        DisplayIdentity left = Twin(OldCard, "5&old", 4352, "AAAA");
        DisplayIdentity right = Twin(OldCard, "5&old", 4353, "BBBB");
        DisplayIdentity newLeft = Twin(NewCard, "5&new", 4355, "AAAA");
        DisplayIdentity newRight = Twin(NewCard, "5&new", 4352, "BBBB");

        TopologyPlan plan = _planner.Plan(TwinDesk(left, right), Snapshot(Attached(newRight), Attached(newLeft)));

        plan.Resolved.Single(r => r.Assignment.Identity == left).Target.Identity.ShouldBe(newLeft);
        plan.Resolved.Single(r => r.Assignment.Identity == right).Target.Identity.ShouldBe(newRight);
        plan.Warnings.Select(w => w.Kind).ShouldBe([PlanWarningKind.MatchedByEdidFallback, PlanWarningKind.MatchedByEdidFallback]);
    }

    [Fact]
    public void Plan_SerialNumberTwoDisplaysShare_TellsNothing()
    {
        // A filler value several monitors report leaves them exactly as ambiguous as their model does.
        DisplayIdentity left = Twin(Gpu, "5&a", 4352, "FILL");
        DisplayIdentity right = Twin(Gpu, "5&a", 4353, "FILL");

        TopologyPlan plan = _planner.Plan(
            TwinDesk(left, right), Snapshot(Attached(Twin(Gpu, "5&a", 4355, "FILL")), Attached(Twin(Gpu, "5&a", 4356, "FILL"))));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Select(m => m.Reason).ShouldBe([MissingReason.Ambiguous, MissingReason.Ambiguous]);
    }

    [Fact]
    public void Plan_SerialNumber_OnlyCountsForTheSameModel()
    {
        DisplayIdentity otherModel = Identity(Gpu, @"\\?\DISPLAY#XEC9999#5&a&0&UID4352#{x}", TwinMaker, 0x9999, "Other") with { EdidSerialHash = "AAAA" };

        TopologyPlan plan = _planner.Plan(
            Profile("Desk", [Mode(Twin(OldCard, "5&old", 4352, "AAAA"), 2560, 1440, 144, primary: true)]), Snapshot(Attached(otherModel)));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Single().Reason.ShouldBe(MissingReason.NotAttached);
    }

    [Fact]
    public void Plan_TwinsAfterACardOrSlotChange_AreMatchedByTheirConnectors()
    {
        // Another slot or a BIOS update gives the card a new identity: every path changes, the ports keep their numbers.
        DisplayIdentity left = Twin(OldCard, "5&old", 4352);
        DisplayIdentity right = Twin(OldCard, "5&old", 4353);
        DisplayIdentity newLeft = Twin(NewCard, "5&new", 4352);
        DisplayIdentity newRight = Twin(NewCard, "5&new", 4353);

        TopologyPlan plan = _planner.Plan(TwinDesk(left, right), Snapshot(Attached(newRight), Attached(newLeft)));

        plan.Resolved.Single(r => r.Assignment.Identity == left).Target.Identity.ShouldBe(newLeft);
        plan.Resolved.Single(r => r.Assignment.Identity == right).Target.Identity.ShouldBe(newRight);
        plan.Warnings.Select(w => w.Kind).ShouldBe([PlanWarningKind.MatchedByEdidFallback, PlanWarningKind.MatchedByEdidFallback]);
    }

    [Fact]
    public void Plan_TwinsByConnector_OnlyWhenEveryOneIsFound()
    {
        // One twin is on a port it never was on: matching the other alone would leave a guess for the rest.
        TopologyPlan plan = _planner.Plan(
            TwinDesk(Twin(OldCard, "5&old", 4352), Twin(OldCard, "5&old", 4353)),
            Snapshot(Attached(Twin(NewCard, "5&new", 4352)), Attached(Twin(NewCard, "5&new", 4355))));

        plan.Resolved.ShouldBeEmpty();
        plan.Missing.Select(m => m.Reason).ShouldBe([MissingReason.Ambiguous, MissingReason.Ambiguous]);
        plan.Warnings.Count(w => w.Kind == PlanWarningKind.AmbiguousTwin).ShouldBe(2);
    }

    [Fact]
    public void Plan_Connector_CountsOnlyWhenTheSavedCardIsGone()
    {
        // The saved card is still there, driving the ultrawide: the same port numbers on another card mean nothing.
        TopologyPlan plan = _planner.Plan(
            TwinDesk(Twin(Gpu, "5&old", 4352), Twin(Gpu, "5&old", 4353)),
            Snapshot(Attached(Ultrawide), Attached(Twin(NewCard, "5&new", 4352)), Attached(Twin(NewCard, "5&new", 4353))));

        plan.Resolved.ShouldBeEmpty();
        plan.IsAmbiguous.ShouldBeTrue();
    }

    [Fact]
    public void Plan_SingleMonitorAfterACardChange_TakesTheOneOnItsConnector()
    {
        // The profile has one of two identical monitors; on the new card only its port says which one.
        DisplayIdentity newLeft = Twin(NewCard, "5&new", 4352);
        DisplayIdentity newRight = Twin(NewCard, "5&new", 4353);

        TopologyPlan plan = _planner.Plan(
            Profile("Right only", [Mode(Twin(OldCard, "5&old", 4353), 2560, 1440, 144, primary: true)]), Snapshot(Attached(newLeft), Attached(newRight)));

        plan.Resolved.Single().Target.Identity.ShouldBe(newRight);
    }

    [Fact]
    public void Plan_OneTwinOffAfterACardChange_AsksToSwitchItOn()
    {
        // Fewer identical monitors attached than the profile misses: one is off, and switching it on can help.
        TopologyPlan plan = _planner.Plan(
            TwinDesk(Twin(OldCard, "5&old", 4352), Twin(OldCard, "5&old", 4353)), Snapshot(Attached(Twin(NewCard, "5&new", 4355))));

        plan.Missing.Select(m => m.Reason).ShouldBe([MissingReason.NotAttached, MissingReason.NotAttached]);
        plan.IsAmbiguous.ShouldBeFalse();
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
