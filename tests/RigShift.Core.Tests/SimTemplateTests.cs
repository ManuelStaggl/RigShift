using RigShift.Core.Games;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class SimTemplateTests
{
    private static GameEntry Entry(GameLaunch launch, string name = "iRacing") => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = launch,
    };

    /// <summary>
    /// iRacing is why the templates exist: its interface stays open all evening, so learning would pick
    /// iRacingUI on the first start – wrong, on the most-used title.
    /// </summary>
    [Fact]
    public void Apply_ForIracing_FillsInBothProcessesAndEndsTheSessionWithTheInterface()
    {
        GameEntry filled = SimTemplates.Apply(Entry(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" }));

        filled.Launch.ProcessName.ShouldBe("iRacingSim64DX11");
        filled.LauncherProcessName.ShouldBe("iRacingUI");
        filled.EndsWith.ShouldBe(SessionEnd.LauncherProcess);
    }

    /// <summary>A sim that is just one program keeps the plain end – there is no interface to outlive it.</summary>
    [Fact]
    public void Apply_ForASimWithoutALauncher_LeavesTheSessionOnTheGameProcess()
    {
        GameEntry filled = SimTemplates.Apply(
            Entry(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "1066890" }, "Automobilista 2"));

        filled.Launch.ProcessName.ShouldBe("AMS2AVX");
        filled.LauncherProcessName.ShouldBeNull();
        filled.EndsWith.ShouldBe(SessionEnd.GameProcess);
    }

    /// <summary>A template that argues with the user is worse than no template.</summary>
    [Fact]
    public void Apply_NeverOverwritesWhatTheUserAlreadySet()
    {
        GameEntry mine = Entry(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", ProcessName = "MyOwnGuess" })
            with
        { LauncherProcessName = "MyOwnLauncher" };

        GameEntry filled = SimTemplates.Apply(mine);

        filled.Launch.ProcessName.ShouldBe("MyOwnGuess");
        filled.LauncherProcessName.ShouldBe("MyOwnLauncher");
    }

    /// <summary>An executable's process name follows from its path, so there is nothing to fill in.</summary>
    [Fact]
    public void Apply_ForAPlainExecutable_ChangesNothing()
    {
        GameEntry entry = Entry(new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\iRacing\iRacingUI.exe" });

        SimTemplates.Apply(entry).Launch.ProcessName.ShouldBeNull();
    }

    [Fact]
    public void Apply_ForAnUnknownGame_ChangesNothing()
    {
        GameEntry entry = Entry(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "999999" }, "Some Indie Racer");

        GameEntry filled = SimTemplates.Apply(entry);

        filled.Launch.ProcessName.ShouldBeNull();
        filled.LauncherProcessName.ShouldBeNull();
    }

    /// <summary>A game that did not come from Steam is matched by its title.</summary>
    [Fact]
    public void For_MatchesByNameWhenThereIsNoSteamId()
        => SimTemplates.For(new GameLaunch { Kind = GameLaunchKind.Epic, Target = "x" }, " assetto corsa ")
            !.GameProcess.ShouldBe("acs");
}
