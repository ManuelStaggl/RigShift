using NSubstitute;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

#pragma warning disable xUnit1051 // NSubstitute matchers stand in for cancellation tokens.

namespace RigShift.Core.Tests;

public sealed class CommandRunnerTests
{
    private static readonly AudioEndpoint Headphones = new("{0.0.0.00000000}.{test-1}", "Headphones");

    private readonly InMemoryProfileStore _store = new();
    private readonly IAudioController _audio = Substitute.For<IAudioController>();
    private readonly IProfileSwitcher _switcher = Substitute.For<IProfileSwitcher>();
    private readonly Profile _desk = Profile("Desk", DeskModes);

    public CommandRunnerTests()
    {
        _store.Profiles.AddRange([_desk, Rig()]);
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Headphones, AudioDirection.Render, IsActive: true, AudioRoleMask.All)]);
    }

    [Fact]
    public async Task List_MarksTheActiveProfile()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.List }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe($"* Desk{Environment.NewLine}  Rig");
    }

    [Fact]
    public async Task List_WithoutDisplayAccess_StillListsProfiles()
    {
        IDisplayConfigurator display = Substitute.For<IDisplayConfigurator>();
        display.QueryAsync(Arg.Any<CancellationToken>())
            .Returns<DisplaySnapshot>(_ => throw new System.ComponentModel.Win32Exception(5));
        var runner = new CommandRunner(_store, display, _audio, Matcher(), Serilog.Core.Logger.None);

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.List }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe($"  Desk{Environment.NewLine}  Rig");
    }

    /// <summary>The report names the grid and its displays; a script reads this over the pipe from another session.</summary>
    [Fact]
    public async Task Surround_ReportsTheRunningGrid()
    {
        var surround = new FakeSurroundController
        {
            ActiveGrid = new SurroundGrid
            {
                Rows = 1,
                Columns = 3,
                Width = 1920,
                Height = 1080,
                RefreshRateHz = 60,
                Displays = [new() { DisplayId = 0x80061086, Name = "CM27X3" }],
            },
        };

        CliResponse response = await Runner(surround: surround).RunAsync(new CliRequest { Command = CliCommand.Surround }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldContain("Surround: on");
        response.Output.ShouldContain("Grid 3x1");
        response.Output.ShouldContain("as 5760x1080");
        response.Output.ShouldContain("80061086");
    }

    /// <summary>On a machine without an NVIDIA driver the command says so instead of failing.</summary>
    [Fact]
    public async Task Surround_WithoutDriver_SaysSo()
    {
        var surround = new FakeSurroundController { Availability = SurroundAvailability.NoDriver };

        CliResponse response = await Runner(surround: surround).RunAsync(new CliRequest { Command = CliCommand.Surround }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldContain("no NVIDIA graphics driver");
    }

    [Fact]
    public async Task Status_NamesActiveProfileAndDisplays()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Status }, CancellationToken.None);

        response.Output.ShouldContain("Active profile: Desk");
        response.Output.ShouldContain("Desk 4K: 3840x2160 @ 165 Hz at (0, 0), primary");
    }

    [Fact]
    public async Task Apply_UnknownProfile_Returns4()
    {
        CliResponse response = await Runner(_switcher).RunAsync(Apply("Couch"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.ProfileNotFound);
        response.Output.ShouldContain("Desk, Rig");
        await _switcher.DidNotReceiveWithAnyArgs().SwitchAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(SwitchOutcome.Applied, CliExitCodes.Applied)]
    [InlineData(SwitchOutcome.AppliedPartially, CliExitCodes.Applied)]
    [InlineData(SwitchOutcome.Failed, CliExitCodes.Failed)]
    [InlineData(SwitchOutcome.Blocked, CliExitCodes.Blocked)]
    [InlineData(SwitchOutcome.RolledBack, CliExitCodes.RolledBack)]
    public async Task Apply_MapsOutcomeToExitCode(SwitchOutcome outcome, int exitCode)
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new SwitchResult { Outcome = outcome, Plan = EmptyPlan(call.Arg<Profile>()) });

        CliResponse response = await Runner(_switcher).RunAsync(Apply("rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(exitCode);
    }

    [Fact]
    public async Task Apply_PassesNoConfirmAndDryRun()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new SwitchResult { Outcome = SwitchOutcome.DryRun, Plan = EmptyPlan(call.Arg<Profile>()) });

        CliResponse response = await Runner(_switcher).RunAsync(
            Apply("Rig") with { NoConfirm = true, DryRun = true, FromLink = true }, CancellationToken.None);

        response.Output.ShouldBe("Rig: ready");
        await _switcher.Received(1).SwitchAsync(
            Arg.Is<Profile>(p => p.Name == "Rig"),
            Arg.Is<SwitchRequest>(r => r.SkipConfirmation && r.DryRun && r.FromLink),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_WhileBusy_Fails()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>()).Returns((SwitchResult?)null);

        CliResponse response = await Runner(_switcher).RunAsync(Apply("Rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("Another switch");
    }

    [Fact]
    public async Task Apply_WithoutApp_Fails()
    {
        CliResponse response = await Runner().RunAsync(Apply("Rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
    }

    [Fact]
    public async Task Toggle_WithoutTarget_Returns4()
    {
        _switcher.ToggleTarget.Returns((Profile?)null);

        CliResponse response = await Runner(_switcher).RunAsync(new CliRequest { Command = CliCommand.Toggle }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.ProfileNotFound);
        response.Output.ShouldContain("No previous profile");
    }

    [Fact]
    public async Task Toggle_SwitchesToTheTargetWithTheOptions()
    {
        Profile rig = Rig();
        _switcher.ToggleTarget.Returns(rig);
        _switcher.SwitchAsync(rig, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwitchResult { Outcome = SwitchOutcome.Applied, Plan = EmptyPlan(rig) });

        CliResponse response = await Runner(_switcher)
            .RunAsync(new CliRequest { Command = CliCommand.Toggle, NoConfirm = true, FromLink = true }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldStartWith("Rig: Applied");
        await _switcher.Received().SwitchAsync(rig, Arg.Is<SwitchRequest>(r => r.SkipConfirmation && r.FromLink && !r.DryRun), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Toggle_WithoutApp_Fails()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Toggle }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
    }

    [Fact]
    public async Task Save_NewName_CapturesDisplaysAndDefaultPlayback()
    {
        CommandRunner runner = Runner();
        bool changed = false;
        runner.ProfilesChanged += (_, _) => changed = true;

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Desk copy" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        changed.ShouldBeTrue();
        Profile saved = _store.Profiles.Single(p => p.Name == "Desk copy");
        saved.Displays.Count.ShouldBe(3);
        saved.Audio.Playback.ShouldBe(Headphones);
    }

    [Fact]
    public async Task Save_ExistingName_UpdatesArrangementAndKeepsIdentity()
    {
        Profile rig = _store.Profiles.Single(p => p.Name == "Rig") with { Icon = "rig", SwitchWithoutAsking = true };
        _store.Profiles[1] = rig;

        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "RIG" }, CancellationToken.None);

        response.Output.ShouldStartWith("Updated profile 'Rig'");
        Profile saved = _store.Profiles.Single(p => p.Id == rig.Id);
        saved.Icon.ShouldBe("rig");
        saved.SwitchWithoutAsking.ShouldBeTrue();
        saved.Displays.Select(d => d.Identity).ShouldBe([DeskLeft, Desk4K, DeskRight]);
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_NoActiveDisplay_Fails()
    {
        var runner = new CommandRunner(_store, new FakeDisplayConfigurator(Snapshot()), _audio, Matcher(), Serilog.Core.Logger.None);

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Empty" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_WhenLoadIncomplete_DoesNotCreateDuplicate()
    {
        _store.Unreadable.Add(new UnreadableProfileFile("locked.json", "The process cannot access the file."));
        CommandRunner runner = Runner();
        bool changed = false;
        runner.ProfilesChanged += (_, _) => changed = true;

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Wheel" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("locked.json");
        changed.ShouldBeFalse();
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_WhenLoadIncomplete_StillUpdatesAFoundProfile()
    {
        _store.Unreadable.Add(new UnreadableProfileFile("locked.json", "The process cannot access the file."));

        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Rig" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Games_MarksTheRunningSession()
    {
        var games = new InMemoryGameStore();
        games.Games.AddRange([Game("iRacing"), Game("Le Mans Ultimate")]);
        var player = Substitute.For<IGamePlayer>();
        player.IsRunning(games.Games[1].Id).Returns(true);

        CliResponse response = await Runner(games: games, player: player)
            .RunAsync(new CliRequest { Command = CliCommand.Games }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe($"  iRacing{Environment.NewLine}* Le Mans Ultimate");
    }

    [Fact]
    public async Task Games_WithoutGames_SaysSo()
    {
        CliResponse response = await Runner(games: new InMemoryGameStore())
            .RunAsync(new CliRequest { Command = CliCommand.Games }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe("No games.");
    }

    /// <summary>An empty list would claim there are no games when the file simply could not be read.</summary>
    [Fact]
    public async Task Games_UnreadableFile_Fails()
    {
        var games = new InMemoryGameStore { Unreadable = "games.json is locked" };

        CliResponse response = await Runner(games: games)
            .RunAsync(new CliRequest { Command = CliCommand.Games }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("games.json is locked");
    }

    [Fact]
    public async Task Play_StartsTheSessionAndReturnsAtOnce()
    {
        var games = new InMemoryGameStore();
        games.Games.Add(Game("iRacing"));
        var player = Substitute.For<IGamePlayer>();
        player.Play(Arg.Any<GameEntry>(), Arg.Any<bool>()).Returns(true);

        CliResponse response = await Runner(games: games, player: player)
            .RunAsync(new CliRequest { Command = CliCommand.Play, GameName = "iracing" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        player.Received(1).Play(Arg.Is<GameEntry>(g => g.Name == "iRacing"), false);
    }

    /// <summary>A web page can send rigshift://play – the session has to know, so its switch asks.</summary>
    [Fact]
    public async Task Play_FromALink_TellsTheSession()
    {
        var games = new InMemoryGameStore();
        games.Games.Add(Game("iRacing"));
        var player = Substitute.For<IGamePlayer>();
        player.Play(Arg.Any<GameEntry>(), Arg.Any<bool>()).Returns(true);

        await Runner(games: games, player: player)
            .RunAsync(new CliRequest { Command = CliCommand.Play, GameName = "iRacing", FromLink = true }, CancellationToken.None);

        player.Received(1).Play(Arg.Any<GameEntry>(), true);
    }

    [Fact]
    public async Task Play_AlreadyRunning_Fails()
    {
        var games = new InMemoryGameStore();
        games.Games.Add(Game("iRacing"));
        var player = Substitute.For<IGamePlayer>();
        player.Play(Arg.Any<GameEntry>(), Arg.Any<bool>()).Returns(false);

        CliResponse response = await Runner(games: games, player: player)
            .RunAsync(new CliRequest { Command = CliCommand.Play, GameName = "iRacing" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("already running");
    }

    [Fact]
    public async Task Play_UnknownGame_ListsTheKnownOnes()
    {
        var games = new InMemoryGameStore();
        games.Games.Add(Game("iRacing"));

        CliResponse response = await Runner(games: games, player: Substitute.For<IGamePlayer>())
            .RunAsync(new CliRequest { Command = CliCommand.Play, GameName = "Solitaire" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.ProfileNotFound);
        response.Output.ShouldContain("iRacing");
    }

    /// <summary>Headless, without a running app, there is nobody who could run a session.</summary>
    [Fact]
    public async Task Play_WithoutAPlayer_Fails()
    {
        var games = new InMemoryGameStore();
        games.Games.Add(Game("iRacing"));

        CliResponse response = await Runner(games: games)
            .RunAsync(new CliRequest { Command = CliCommand.Play, GameName = "iRacing" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("not running");
    }

    private static GameEntry Game(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
    };

    private static CliRequest Apply(string name) => new() { Command = CliCommand.Apply, ProfileName = name };

    private static TopologyPlan EmptyPlan(Profile profile) => new() { Profile = profile, Resolved = [], Missing = [], Warnings = [] };

    private static ActiveProfileMatcher Matcher() => new(new TopologyPlanner(new TopologyPlannerOptions()));

    private CommandRunner Runner(
        IProfileSwitcher? switcher = null,
        ISurroundController? surround = null,
        IGameStore? games = null,
        IGamePlayer? player = null) =>
        new(_store, new FakeDisplayConfigurator(DeskActive()), _audio, Matcher(), Serilog.Core.Logger.None, switcher, surround, games, player);
}
