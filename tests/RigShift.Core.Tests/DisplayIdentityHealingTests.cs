using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class DisplayIdentityHealingTests
{
    private static readonly DisplayIdentity Moved = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEWPORT&7" };

    [Fact]
    public void Find_MonitorOnAnotherPort_IsAMove()
    {
        Profile desk = Profile("Desk", DeskModes);

        IdentityUpdate update = DisplayIdentityHealing.Find(PlanOf(desk, (DeskModes[1], Moved)), [desk]).ShouldHaveSingleItem();

        update.SavedPath.ShouldBe(DeskLeft.TargetDevicePath);
        update.Identity.ShouldBe(Moved);
        update.Moved.ShouldBeTrue();
    }

    [Fact]
    public void Find_SerialNumberTheProfileLacks_IsLearnedWithoutAMove()
    {
        // Profiles from before 4.0 know no serial numbers; the first switch that stays fills them in.
        Profile desk = Profile("Desk", DeskModes);
        DisplayIdentity withSerial = DeskLeft with { EdidSerialHash = "0123456789ABCDEF" };

        IdentityUpdate update = DisplayIdentityHealing.Find(PlanOf(desk, (DeskModes[1], withSerial)), [desk]).ShouldHaveSingleItem();

        update.Moved.ShouldBeFalse();
        update.Identity.ShouldBe(withSerial);
    }

    [Fact]
    public void Find_SameMonitorSpelledDifferently_IsNothingNew()
    {
        Profile desk = Profile("Desk", DeskModes);
        DisplayIdentity upper = DeskLeft with { TargetDevicePath = DeskLeft.TargetDevicePath.ToUpperInvariant() };

        DisplayIdentityHealing.Find(PlanOf(desk, (DeskModes[0], Desk4K), (DeskModes[1], upper)), [desk]).ShouldBeEmpty();
    }

    [Fact]
    public void Find_KeepsWhatTheDisplayDoesNotReport()
    {
        // A failed EDID read must not wipe the model, serial number or name the profile knows.
        DisplayIdentity saved = DeskLeft with { EdidSerialHash = "AAAA" };
        DisplayIdentity live = Moved with { EdidManufacturerId = 0, EdidProductCodeId = 0, EdidSerialHash = null, FriendlyName = string.Empty };
        Profile desk = Profile("Desk", [DeskModes[0], DeskModes[1] with { Identity = saved }]);

        IdentityUpdate update = DisplayIdentityHealing.Find(PlanOf(desk, (desk.Displays[1], live)), [desk]).ShouldHaveSingleItem();

        update.Identity.ShouldBe(saved with { TargetDevicePath = Moved.TargetDevicePath });
    }

    [Fact]
    public void Find_MoveOntoAPathAnotherProfileUses_IsLeftOut()
    {
        // The desk's left monitor is off and the planner took its twin, which the side profile has. Writing that down would
        // tie the desk to the wrong monitor for good.
        Profile desk = Profile("Desk", [DeskModes[1]]);
        Profile side = Profile("Side", [Mode(Moved, 1920, 1080, 60, primary: true)]);

        DisplayIdentityHealing.Find(PlanOf(desk, (DeskModes[1], Moved)), [desk, side]).ShouldBeEmpty();
    }

    [Fact]
    public void Apply_RewritesEveryProfileWithTheMonitor_AndOnlyThose()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile left = Profile("Left", [DeskModes[1] with { IsPrimary = true, CustomName = "Left" }]);

        IReadOnlyList<Profile> changed = DisplayIdentityHealing.Apply([desk, left, Rig()], [new IdentityUpdate(DeskLeft.TargetDevicePath, Moved)]);

        changed.Select(p => p.Name).ShouldBe(["Desk", "Left"]);
        changed[0].Displays.Select(d => d.Identity).ShouldBe([Desk4K, Moved, DeskRight]);
        changed[1].Displays.Single().ShouldBe(left.Displays[0] with { Identity = Moved });
    }

    [Fact]
    public void Rekey_MovesEntriesToTheNewPath()
    {
        var names = new Dictionary<string, string>
        {
            [DeskLeft.TargetDevicePath.ToUpperInvariant()] = "Left",
            [DeskRight.TargetDevicePath] = "Right",
        };

        IReadOnlyDictionary<string, string> rekeyed =
            DisplayIdentityHealing.Rekey(names, [new IdentityUpdate(DeskLeft.TargetDevicePath, Moved)]).ShouldNotBeNull();

        rekeyed[Moved.TargetDevicePath].ShouldBe("Left");
        rekeyed.ContainsKey(DeskLeft.TargetDevicePath).ShouldBeFalse();
        rekeyed[DeskRight.TargetDevicePath].ShouldBe("Right");
    }

    [Fact]
    public void Rekey_KeepsWhatTheNewPathHas_AndTheTableWhenNothingMoved()
    {
        var names = new Dictionary<string, string> { [DeskLeft.TargetDevicePath] = "Old", [Moved.TargetDevicePath] = "New" };
        var learned = new IdentityUpdate(DeskLeft.TargetDevicePath, DeskLeft with { EdidSerialHash = "AAAA" });

        DisplayIdentityHealing.Rekey(names, [new IdentityUpdate(DeskLeft.TargetDevicePath, Moved)]).ShouldNotBeNull()[Moved.TargetDevicePath].ShouldBe("New");
        DisplayIdentityHealing.Rekey(names, [learned]).ShouldBeSameAs(names);
        DisplayIdentityHealing.Rekey<string>(null, [learned]).ShouldBeNull();
    }

    private static TopologyPlan PlanOf(Profile profile, params (DisplayAssignment Assignment, DisplayIdentity Live)[] resolved) =>
        new()
        {
            Profile = profile,
            Resolved = [.. resolved.Select(r => new PlannedDisplay(r.Assignment, Attached(r.Live)))],
            Missing = [],
            Warnings = [],
        };
}
