using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class HotkeyConflictsTests
{
    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };

    private static readonly Profile Rig = new() { Id = Guid.NewGuid(), Name = "Rig", Displays = [], Hotkey = CtrlAltR };
    private static readonly Profile Desk = new() { Id = Guid.NewGuid(), Name = "Desk", Displays = [] };

    private static readonly GameEntry Iracing = new()
    {
        Id = Guid.NewGuid(),
        Name = "iRacing",
        Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\Sims\iRacing.exe" },
        Hotkey = CtrlAltD,
    };

    private static HotkeyUse Self(HotkeyUseKind kind, Guid id) => new(kind, id, null);

    [Fact]
    public void Find_GameWantsAProfilesHotkey_NamesTheProfile()
    {
        HotkeyUse? use = HotkeyConflicts.Find(CtrlAltR, Self(HotkeyUseKind.Game, Iracing.Id), [Rig, Desk], [Iracing], toggle: null);

        use.ShouldBe(new HotkeyUse(HotkeyUseKind.Profile, Rig.Id, "Rig"));
    }

    [Fact]
    public void Find_ProfileWantsAGamesHotkey_NamesTheGame()
    {
        HotkeyUse? use = HotkeyConflicts.Find(CtrlAltD, Self(HotkeyUseKind.Profile, Desk.Id), [Rig, Desk], [Iracing], toggle: null);

        use.ShouldBe(new HotkeyUse(HotkeyUseKind.Game, Iracing.Id, "iRacing"));
    }

    [Fact]
    public void Find_ToggleWantsAProfilesHotkey_AndTheOtherWayRound()
    {
        HotkeyConflicts.Find(CtrlAltR, Self(HotkeyUseKind.Toggle, Guid.Empty), [Rig], [], toggle: null)
            .ShouldNotBeNull().Kind.ShouldBe(HotkeyUseKind.Profile);
        HotkeyConflicts.Find(CtrlAltD, Self(HotkeyUseKind.Profile, Desk.Id), [Rig, Desk], [], toggle: CtrlAltD)
            .ShouldBe(new HotkeyUse(HotkeyUseKind.Toggle, Guid.Empty, null));
    }

    [Fact]
    public void Find_TheOwnHotkey_IsNoConflict()
    {
        HotkeyConflicts.Find(CtrlAltR, Self(HotkeyUseKind.Profile, Rig.Id), [Rig, Desk], [Iracing], toggle: null).ShouldBeNull();
        HotkeyConflicts.Find(CtrlAltD, Self(HotkeyUseKind.Game, Iracing.Id), [Rig], [Iracing], toggle: null).ShouldBeNull();
        HotkeyConflicts.Find(CtrlAltD, Self(HotkeyUseKind.Toggle, Guid.Empty), [Rig], [], toggle: CtrlAltD).ShouldBeNull();
    }

    /// <summary>A game and a profile may carry the same id; the kind keeps them apart.</summary>
    [Fact]
    public void Find_SameIdButOtherKind_IsAConflict()
    {
        GameEntry twin = Iracing with { Id = Rig.Id, Hotkey = CtrlAltR };

        HotkeyConflicts.Find(CtrlAltR, Self(HotkeyUseKind.Game, twin.Id), [Rig], [twin], toggle: null)
            .ShouldNotBeNull().Kind.ShouldBe(HotkeyUseKind.Profile);
    }

    [Fact]
    public void All_LeavesOutInvalidHotkeys()
    {
        Profile shiftOnly = Desk with { Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Shift, VirtualKey = 0x41 } };

        HotkeyConflicts.All([Rig, shiftOnly], [Iracing], toggle: null).Select(u => u.Use.Name).ShouldBe(["Rig", "iRacing"]);
    }
}
