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
