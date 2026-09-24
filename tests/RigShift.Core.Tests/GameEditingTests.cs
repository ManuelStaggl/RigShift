using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class GameEditingTests
{
    private static readonly Hotkey CtrlAltF1 = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 };

    [Fact]
    public void FindByName_IgnoresCaseAndBlanks()
    {
        GameEntry iracing = Game("iRacing");

        GameEditing.FindByName([Game("Assetto Corsa"), iracing], " IRACING ").ShouldBe(iracing);
        GameEditing.FindByName([iracing], "iRacing 2").ShouldBeNull();
    }

    [Fact]
    public void Validate_ValidGame_HasNoProblems()
    {
        GameEntry iracing = Game("iRacing") with { Hotkey = CtrlAltF1 };

        GameEditing.Validate(iracing, [iracing, Game("Assetto Corsa")], [Rig()]).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ReportsNameProblems()
    {
        GameEntry ac = Game("Assetto Corsa");

        GameEditing.Validate(Game(" "), [], []).ShouldBe([GameProblem.NameMissing]);
        GameEditing.Validate(Game(new string('x', GameEntry.MaxNameLength)), [], []).ShouldBeEmpty();
        GameEditing.Validate(Game(new string('x', GameEntry.MaxNameLength + 1)), [], []).ShouldBe([GameProblem.NameTooLong]);
        GameEditing.Validate(Game(" assetto corsa "), [ac], []).ShouldBe([GameProblem.NameTaken]);
        GameEditing.Validate(ac with { Name = "ASSETTO CORSA" }, [ac], []).ShouldBeEmpty();
    }

    [Fact]
    public void Validate_ReportsNothingToStart()
    {
        GameEntry game = Game("iRacing") with { Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = " " } };

        GameEditing.Validate(game, [], []).ShouldBe([GameProblem.LaunchMissing]);
    }

    /// <summary>Windows registers a combination once: whichever of the two owners comes second would never fire.</summary>
    [Fact]
    public void Validate_ReportsHotkeyOfAnotherGameOrAProfile_AndOneWithoutModifier()
    {
        GameEntry ac = Game("Assetto Corsa") with { Hotkey = CtrlAltF1 };
        Profile rig = Rig() with { Hotkey = CtrlAltF1 with { VirtualKey = 0x71 } };

        GameEditing.Validate(Game("iRacing") with { Hotkey = CtrlAltF1 with { } }, [ac], []).ShouldBe([GameProblem.HotkeyTaken]);
        GameEditing.Validate(Game("iRacing") with { Hotkey = rig.Hotkey }, [], [rig]).ShouldBe([GameProblem.HotkeyTaken]);
        GameEditing.Validate(ac, [ac], [rig]).ShouldBeEmpty();
        GameEditing.Validate(Game("iRacing") with { Hotkey = CtrlAltF1 with { Modifiers = HotkeyModifiers.None } }, [ac], [])
            .ShouldBe([GameProblem.HotkeyInvalid]);
    }

    [Fact]
    public void Validate_ReportsToolWithoutProgram()
    {
        GameEntry game = Game("iRacing") with { Apps = [new AppAction { Path = @"C:\Tools\SimHub.exe" }, new AppAction { Path = " " }] };

        GameEditing.Validate(game, [], []).ShouldBe([GameProblem.AppPathMissing]);
    }

    private static GameEntry Game(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
    };
}
