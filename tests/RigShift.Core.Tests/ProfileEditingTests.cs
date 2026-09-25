using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class ProfileEditingTests
{
    [Fact]
    public void Capture_TakesOnlyActiveDisplaysLeftToRight()
    {
        Profile profile = ProfileEditing.Capture("  Desk ", DeskActive());

        profile.Name.ShouldBe("Desk");
        profile.Displays.Select(d => d.Identity).ShouldBe([DeskLeft, Desk4K, DeskRight]);
        profile.Displays.Single(d => d.IsPrimary).Identity.ShouldBe(Desk4K);
        profile.Displays.ShouldAllBe(d => !d.IsOptional);
    }

    [Fact]
    public void CurrentArrangement_KeepsOptionalFlagOfKnownDisplays()
    {
        DisplaySnapshot rigActive = Snapshot(
            Attached(Ultrawide, activeMode: UltrawideMode),
            Attached(Tablet, activeMode: TabletMode with { IsOptional = false, PositionX = 5120 }));

        IReadOnlyList<DisplayAssignment> arrangement = ProfileEditing.CurrentArrangement(rigActive, [TabletMode]);

        arrangement.Single(d => d.Identity == Tablet).IsOptional.ShouldBeTrue();
        arrangement.Single(d => d.Identity == Ultrawide).IsOptional.ShouldBeFalse();
    }

    [Fact]
    public void Capture_LeavesHdrAsWindowsHasIt()
    {
        // K-12: a captured profile that sets HDR turned every change in Windows back on the next switch.
        DisplaySnapshot hdrOn = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = true }));

        ProfileEditing.Capture("Rig", hdrOn).Displays.Single().Hdr.ShouldBeNull();
    }

    [Fact]
    public void CapturedArrangement_KeepsTheProfilesHdrChoice()
    {
        DisplaySnapshot live = Snapshot(
            Attached(Desk4K, activeMode: DeskModes[0] with { Hdr = true }),
            Attached(DeskLeft, activeMode: DeskModes[1] with { Hdr = true }),
            Attached(DeskRight, activeMode: DeskModes[2] with { Hdr = true }));

        IReadOnlyList<DisplayAssignment> arrangement = ProfileEditing.CapturedArrangement(
            live, [DeskModes[0] with { Hdr = false }, DeskModes[1] with { Hdr = null }]);

        arrangement.Single(d => d.Identity == Desk4K).Hdr.ShouldBe(false);
        arrangement.Single(d => d.Identity == DeskLeft).Hdr.ShouldBeNull();
        arrangement.Single(d => d.Identity == DeskRight).Hdr.ShouldBeNull();
    }

    [Fact]
    public void SetPrimary_ShiftsAllPositionsSoThePrimaryIsAtOrigin()
    {
        IReadOnlyList<DisplayAssignment> displays = [DeskModes[1], DeskModes[0], DeskModes[2]];

        IReadOnlyList<DisplayAssignment> result = ProfileEditing.SetPrimary(displays, 0);

        result.Select(d => d.PositionX).ShouldBe([0, 1920, 5760]);
        result.Select(d => d.IsPrimary).ShouldBe([true, false, false]);
    }

    [Fact]
    public void SetPrimary_OptionalDisplayBecomesRequired()
    {
        IReadOnlyList<DisplayAssignment> result = ProfileEditing.SetPrimary([UltrawideMode, TabletMode], 1);

        result[1].IsOptional.ShouldBeFalse();
        result[1].PositionX.ShouldBe(0);
        result[0].PositionX.ShouldBe(-5120);
    }

    [Fact]
    public void ToggleTarget_PrefersThePreviousProfile_ThenTheDefault()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig();
        Profile tv = Profile("TV", DeskModes);
        Profile[] all = [desk, rig, tv];

        ProfileEditing.ToggleTarget(all, rig.Id, desk.Id, tv.Id).ShouldBe(desk);
        ProfileEditing.ToggleTarget(all, rig.Id, null, tv.Id).ShouldBe(tv);
        ProfileEditing.ToggleTarget(all, null, null, tv.Id).ShouldBe(tv);
    }

    [Fact]
    public void ToggleTarget_SkipsTheActiveAndDeletedProfiles()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig();
        Profile[] all = [desk, rig];

        // Previous is active again (Windows restored it): fall through to the default.
        ProfileEditing.ToggleTarget(all, desk.Id, desk.Id, rig.Id).ShouldBe(rig);
        // Previous was deleted, default is the active one: nowhere to go.
        ProfileEditing.ToggleTarget(all, desk.Id, Guid.NewGuid(), desk.Id).ShouldBeNull();
        ProfileEditing.ToggleTarget(all, desk.Id, null, null).ShouldBeNull();
    }

    [Fact]
    public void FindByName_IgnoresCaseAndBlanks()
    {
        Profile rig = Rig();

        ProfileEditing.FindByName([Profile("Desk", DeskModes), rig], " rig ").ShouldBe(rig);
        ProfileEditing.FindByName([rig], "Rigs").ShouldBeNull();
    }

    [Fact]
    public void UniqueName_AppendsTheFirstFreeNumber()
    {
        ProfileEditing.UniqueName("Rig", ["Desk"]).ShouldBe("Rig");
        ProfileEditing.UniqueName("Rig", ["rig", "Rig 2"]).ShouldBe("Rig 3");
    }

    [Fact]
    public void Validate_ValidProfile_HasNoProblems()
    {
        Profile rig = Rig();

        ProfileEditing.Validate(rig, [rig, Profile("Desk", DeskModes)]).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ReportsNameAndPrimaryProblems()
    {
        Profile desk = Profile("Desk", DeskModes);

        ProfileEditing.Validate(Rig() with { Name = " " }, []).ShouldBe([ProfileProblem.NameMissing]);
        ProfileEditing.Validate(Rig() with { Name = new string('x', Profiles.Profile.MaxNameLength) }, []).ShouldBeEmpty();
        ProfileEditing.Validate(Rig() with { Name = new string('x', Profiles.Profile.MaxNameLength + 1) }, []).ShouldBe([ProfileProblem.NameTooLong]);
        ProfileEditing.Validate(Rig() with { Name = "desk" }, [desk]).ShouldBe([ProfileProblem.NameTaken]);
        ProfileEditing.Validate(Rig() with { Displays = [] }, []).ShouldBe([ProfileProblem.NoDisplays]);
        ProfileEditing.Validate(Rig() with { Displays = [TabletMode] }, []).ShouldBe([ProfileProblem.NoSinglePrimary]);
        ProfileEditing.Validate(Rig() with { Displays = [UltrawideMode with { IsOptional = true }] }, [])
            .ShouldBe([ProfileProblem.PrimaryIsOptional]);
    }

    [Fact]
    public void Validate_ReportsHotkeyProblems()
    {
        var ctrlAltF1 = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 };
        Profile desk = Profile("Desk", DeskModes) with { Hotkey = ctrlAltF1 };

        ProfileEditing.Validate(Rig() with { Hotkey = ctrlAltF1 with { } }, [desk]).ShouldBe([ProfileProblem.HotkeyTaken]);
        ProfileEditing.Validate(desk, [desk]).ShouldBeEmpty();
        ProfileEditing.Validate(Rig() with { Hotkey = ctrlAltF1 with { Modifiers = HotkeyModifiers.None } }, [])
            .ShouldBe([ProfileProblem.HotkeyInvalid]);
    }

    [Fact]
    public void Validate_ReportsAppWithoutProgram()
    {
        Profile rig = Rig() with { Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }, new AppAction { Path = "  " }] };

        ProfileEditing.Validate(rig, []).ShouldBe([ProfileProblem.AppPathMissing]);
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control, 0x70, true)]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Shift, 0x31, true)]
    [InlineData(HotkeyModifiers.None, 0x70, false)]
    [InlineData(HotkeyModifiers.Shift, 0x41, false)]
    [InlineData(HotkeyModifiers.Control, 0x11, false)]
    [InlineData(HotkeyModifiers.Alt, 0xA4, false)]
    [InlineData(HotkeyModifiers.Alt, 0, false)]
    [InlineData((HotkeyModifiers)0x4000, 0x70, false)]
    public void Hotkey_IsValid_NeedsAModifierAndAnOrdinaryKey(HotkeyModifiers modifiers, int virtualKey, bool expected)
    {
        new Hotkey { Modifiers = modifiers, VirtualKey = virtualKey }.IsValid.ShouldBe(expected);
    }
}
