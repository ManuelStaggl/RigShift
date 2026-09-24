using NSubstitute;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

// NSubstitute matchers stand in for the token, which xUnit1051 cannot tell from a forgotten one.
#pragma warning disable xUnit1051

public sealed class GameSessionRunnerTests
{
    private const string InstallFolder = @"D:\Steam\steamapps\common\iRacing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Profile _rig = Rig();
    private readonly Profile _desk = Rig() with { Id = Guid.NewGuid(), Name = "Desk" };
    private readonly IGameStarter _starter = Substitute.For<IGameStarter>();
    private readonly IProfileSwitcher _switcher = Substitute.For<IProfileSwitcher>();
    private readonly IAppLauncher _apps = Substitute.For<IAppLauncher>();
    private readonly IUsbDeviceList _usb = Substitute.For<IUsbDeviceList>();
    private readonly FakeGameProcesses _processes = new();
    private readonly AutoAdvanceTimeProvider _time = new();

    public GameSessionRunnerTests()
    {
        _usb.PresentDeviceIds().Returns(new HashSet<string>());
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result(SwitchOutcome.Applied));
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());
    }

    private FakeRunningGame LongRunning() => new(_time, TimeSpan.FromHours(1));

    private GameSessionRunner Runner(FakeWindowLayout? desktop = null) => new(
        _starter, _processes, _switcher, id => new[] { _rig, _desk }.FirstOrDefault(p => p.Id == id),
        _apps, _usb, new SwitchOptions(), _time, Logger.None, desktop);

    private GameEntry Game(GameLaunch? launch = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "iRacing",
        ProfileId = _rig.Id,
        Launch = launch ?? new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\Games\iRacing\iRacing.exe" },
    };

    private static Task<SwitchResult?> Result(SwitchOutcome outcome) => Task.FromResult<SwitchResult?>(new SwitchResult
    {
        Outcome = outcome,
        Plan = new TopologyPlan { Profile = Rig(), Resolved = [], Missing = [], Warnings = [] },
    });

    [Fact]
    public async Task Run_SwitchesStartsTheAppsAndTheGame_AndStaysWhenThatIsTheExitAction()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe" } },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.Profile.ShouldBe(SwitchOutcome.Applied);
        result.Apps.ShouldBe(AppsOutcome.Applied);
        result.Exit.ShouldBeNull();
        await _switcher.Received(1).SwitchAsync(_rig, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
        _apps.Received().Start(@"C:\SimHub\SimHubWPF.exe", null);
    }

    [Fact]
    public async Task Run_WaitsForTheProfilesApps_BeforeTheGameAndItsTools()
    {
        // K-11: the wheelbase software is in the profile and waits for the wheelbase; the game must not start before it.
        var profileApps = new TaskCompletionSource<AppsOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SwitchResult?>(new SwitchResult
            {
                Outcome = SwitchOutcome.Applied,
                Plan = new TopologyPlan { Profile = Rig(), Resolved = [], Missing = [], Warnings = [] },
                Apps = AppsOutcome.Pending,
                AppsCompletion = profileApps.Task,
            }));
        GameEntry game = Game() with
        {
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"C:\Tools\Before.exe" } },
        };

        Task<GameSessionResult> session = Runner().RunAsync(game, alreadyRunning: false, Ct);

        session.IsCompleted.ShouldBeFalse();
        _starter.DidNotReceiveWithAnyArgs().Start(default!);
        _apps.DidNotReceiveWithAnyArgs().Start(default!, default);
        profileApps.SetResult(AppsOutcome.Applied);
        (await session).Outcome.ShouldBe(GameSessionOutcome.Ended);
        _starter.ReceivedWithAnyArgs(1).Start(default!);
    }

    /// <summary>Starting a game into a layout that was not applied is worse than not starting it.</summary>
    [Fact]
    public async Task Run_DoesNotStartTheGameWhenTheProfileWasBlocked()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result(SwitchOutcome.Blocked));

        GameSessionResult result = await Runner().RunAsync(Game(), alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.ProfileFailed);
        result.Profile.ShouldBe(SwitchOutcome.Blocked);
        _starter.DidNotReceive().Start(Arg.Any<GameLaunch>());
    }

    /// <summary>A rigshift://play link: the switch asks even with confirmation off, and "no" starts nothing.</summary>
    [Fact]
    public async Task Run_FromALink_AsksAndStartsNothingWhenDeclined()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Result(SwitchOutcome.RolledBack));

        GameSessionResult result = await Runner().RunAsync(Game(), alreadyRunning: false, fromLink: true, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.ProfileFailed);
        await _switcher.Received(1).SwitchAsync(Arg.Any<Profile>(), Arg.Is<SwitchRequest>(r => r.FromLink), Arg.Any<CancellationToken>());
        _starter.DidNotReceive().Start(Arg.Any<GameLaunch>());
    }

    [Fact]
    public async Task Run_WithoutALink_SwitchesWithTheDefaultRequest()
    {
        await Runner().RunAsync(Game(), alreadyRunning: false, Ct);

        await _switcher.Received(1).SwitchAsync(Arg.Any<Profile>(), Arg.Is<SwitchRequest>(r => !r.FromLink), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_TreatsAnotherRunningSwitchAsBlocked()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<SwitchResult?>(null));

        (await Runner().RunAsync(Game(), alreadyRunning: false, Ct)).Outcome.ShouldBe(GameSessionOutcome.ProfileFailed);
    }

    /// <summary>A store launch hands us the client, so the game's own process has to be learned and kept.</summary>
    [Fact]
    public async Task Run_LearnsTheProcessNameOfAStoreLaunch_AndReportsIt()
    {
        GameEntry game = Game(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", InstallFolder = InstallFolder });
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        // Not before the second call: the first one is the snapshot of everything that was already there.
        _processes.OnListed = call =>
        {
            if (call == 2)
            {
                _processes.Running.Add(new RunningProcess(9, "iRacingSim64DX11", InstallFolder + @"\iRacingSim64DX11.exe", _time.GetUtcNow()));
            }
        };
        GameProcessLearned? learned = null;
        GameSessionRunner runner = Runner();
        runner.ProcessLearned += (_, e) => learned = e;

        GameSessionResult result = await runner.RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.LearnedProcessName.ShouldBe("iRacingSim64DX11");
        learned.ShouldNotBeNull();
        learned.GameId.ShouldBe(game.Id);
        learned.ProcessName.ShouldBe("iRacingSim64DX11");
    }

    /// <summary>Once the name is known there is nothing left to learn – the second start goes straight to waiting.</summary>
    [Fact]
    public async Task Run_WithAKnownProcessName_DoesNotLearnAgain()
    {
        GameEntry game = Game(new GameLaunch
        {
            Kind = GameLaunchKind.Steam,
            Target = "266410",
            InstallFolder = InstallFolder,
            ProcessName = "iRacingSim64DX11",
        });
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        _processes.Running.Add(new RunningProcess(9, "iRacingSim64DX11", null, _time.GetUtcNow()));
        _processes.OnIsRunning = call =>
        {
            if (call >= 3)
            {
                _processes.Running.Clear();
            }
        };

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.LearnedProcessName.ShouldBeNull();
        _time.Elapsed.ShouldBeLessThan(GameProcessLearner.Timeout);
    }

    /// <summary>
    /// Steam needs ten to forty seconds to bring a game up. Polling for "gone" right after the start saw "gone" at
    /// once: the session ended and the displays switched back while the game was still loading.
    /// </summary>
    [Fact]
    public async Task Run_WithAKnownProcessName_WaitsForTheGameToShowUpBeforeWaitingForItsEnd()
    {
        GameEntry game = KnownSteamGame() with { Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = _desk.Id } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        bool wasUp = false;
        _processes.OnIsRunning = call =>
        {
            if (call == 10)
            {
                _processes.Running.Add(new RunningProcess(9, "iRacingSim64DX11", null, _time.GetUtcNow()));
                wasUp = true;
            }
            else if (call == 15)
            {
                _processes.Running.Clear();
            }
        };
        _switcher.When(s => s.SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>()))
            .Do(_ => wasUp.ShouldBeTrue("the exit action ran before the game had come up"));

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _processes.IsRunningCalls.ShouldBeGreaterThanOrEqualTo(15);
        await _switcher.Received(1).SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>A game that never shows up has not ended: nothing is switched back under whatever is running instead.</summary>
    [Fact]
    public async Task Run_WithAKnownProcessNameThatNeverShowsUp_IsNotRecognisedAndSwitchesNothingBack()
    {
        GameEntry game = KnownSteamGame() with { Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = _desk.Id } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.NotRecognised);
        _time.Elapsed.ShouldBeGreaterThanOrEqualTo(GameProcessLearner.Timeout);
        await _switcher.DidNotReceive().SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An update renamed the executable: the stale name never shows up, but the game's folder gives it away.</summary>
    [Fact]
    public async Task Run_WhenTheKnownNameNeverShowsUpButTheGameRuns_LearnsTheNewName()
    {
        GameEntry game = KnownSteamGame();
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        _processes.OnListed = call =>
        {
            if (call == 2)
            {
                _processes.Running.Add(new RunningProcess(9, "iRacingSim64DX12", InstallFolder + @"\iRacingSim64DX12.exe", _time.GetUtcNow()));
            }
        };
        GameProcessLearned? learned = null;
        GameSessionRunner runner = Runner();
        runner.ProcessLearned += (_, e) => learned = e;

        GameSessionResult result = await runner.RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.LearnedProcessName.ShouldBe("iRacingSim64DX12");
        learned.ShouldNotBeNull();
    }

    /// <summary>The interface is started by the store like the game is, so it needs the same patience.</summary>
    [Fact]
    public async Task Run_ForASimWhoseInterfaceOutlivesIt_WaitsForTheInterfaceToShowUpFirst()
    {
        GameEntry game = KnownSteamGame() with { EndsWith = SessionEnd.LauncherProcess, LauncherProcessName = "iRacingUI" };
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        _processes.OnIsRunning = call =>
        {
            if (call == 10)
            {
                _processes.Running.Add(new RunningProcess(1, "iRacingUI", null, _time.GetUtcNow()));
            }
            else if (call == 15)
            {
                _processes.Running.Clear();
            }
        };

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _processes.IsRunningCalls.ShouldBeGreaterThanOrEqualTo(15);
    }

    /// <summary>The handle from the start is kept: a process id looked up again later may belong to someone else by then.</summary>
    [Fact]
    public async Task Run_WaitsOnTheStartedProcessItself_AndLetsGoOfItAfterwards()
    {
        var started = new FakeRunningGame(_time, TimeSpan.FromHours(1));
        _starter.Start(Arg.Any<GameLaunch>()).Returns(started);

        GameSessionResult result = await Runner().RunAsync(Game(), alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _time.Elapsed.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromHours(1));
        _processes.WaitedFor.ShouldBeEmpty();
        _processes.IsRunningCalls.ShouldBe(0);
        started.Disposed.ShouldBeTrue();
    }

    /// <summary>
    /// A launcher stub: the executable we started is gone after two seconds and the game runs on under the same name.
    /// Taking the stub's end for the game's switched the displays back under the running game.
    /// </summary>
    [Fact]
    public async Task Run_WhenTheStartedProcessEndsAtOnceButTheGameRunsOn_WaitsForTheGame()
    {
        GameEntry game = Game() with { Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = _desk.Id } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(new FakeRunningGame(_time, TimeSpan.FromSeconds(2)));
        _processes.Running.Add(new RunningProcess(9, "iRacing", null, _time.GetUtcNow()));
        bool gone = false;
        _processes.OnIsRunning = call =>
        {
            if (call >= 5)
            {
                _processes.Running.Clear();
                gone = true;
            }
        };
        _switcher.When(s => s.SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>()))
            .Do(_ => gone.ShouldBeTrue("the exit action ran while the game was still up"));

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _processes.IsRunningCalls.ShouldBeGreaterThanOrEqualTo(5);
        await _switcher.Received(1).SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The stub starts a game with another name; what is new and runs from the game's folder is the game.</summary>
    [Fact]
    public async Task Run_WhenTheStartedProcessEndsAtOnceAndAnotherOneFollows_WaitsForThatOne()
    {
        _starter.Start(Arg.Any<GameLaunch>()).Returns(new FakeRunningGame(_time, TimeSpan.FromSeconds(2)));
        _processes.Running.Add(new RunningProcess(3, "Explorer", @"C:\Windows\explorer.exe", _time.GetUtcNow()));
        _processes.OnListed = call =>
        {
            if (call == 3)
            {
                _processes.Running.Add(new RunningProcess(9, "iRacingSim64DX11", @"C:\Games\iRacing\bin\iRacingSim64DX11.exe", _time.GetUtcNow()));
            }
        };

        GameSessionResult result = await Runner().RunAsync(Game(), alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _processes.WaitedFor.ShouldBe([9]);
    }

    /// <summary>A game that crashes on start has ended: after a short look for a successor the session goes on to its end.</summary>
    [Fact]
    public async Task Run_WhenTheStartedProcessEndsAtOnceAndNothingFollows_EndsTheSession()
    {
        GameEntry game = Game() with { Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = _desk.Id } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(new FakeRunningGame(_time, TimeSpan.FromSeconds(2)));

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _time.Elapsed.ShouldBeLessThan(GameProcessLearner.Timeout);
        await _switcher.Received(1).SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    private GameEntry KnownSteamGame() => Game(new GameLaunch
    {
        Kind = GameLaunchKind.Steam,
        Target = "266410",
        InstallFolder = InstallFolder,
        ProcessName = "iRacingSim64DX11",
    });

    [Fact]
    public async Task Run_WhenNothingCanBeRecognised_SaysSo()
    {
        GameEntry game = Game(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", InstallFolder = InstallFolder });
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.NotRecognised);
    }

    [Fact]
    public async Task Run_ReportsAFailedStart()
    {
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => throw new System.ComponentModel.Win32Exception("gone"));

        (await Runner().RunAsync(Game(), alreadyRunning: false, Ct)).Outcome.ShouldBe(GameSessionOutcome.StartFailed);
    }

    /// <summary>The automation case: the game was started from Steam, so only the bracket around it runs.</summary>
    [Fact]
    public async Task Run_ForAGameThatAlreadyRuns_AppliesTheProfileButStartsNothing()
    {
        GameEntry game = Game(new GameLaunch
        {
            Kind = GameLaunchKind.Steam,
            Target = "266410",
            ProcessName = "iRacingSim64DX11",
        });

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: true, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _starter.DidNotReceive().Start(Arg.Any<GameLaunch>());
        await _switcher.Received(1).SwitchAsync(_rig, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_EndsTheStartedAppsWhenTheGameIsOver()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction>
            {
                new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe" },
                new() { Kind = AppActionKind.Stop, Path = @"C:\Other\Nagging.exe" },
            },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());
        _apps.IsRunning(Arg.Any<string>()).Returns(false, true, true);

        await Runner().RunAsync(game, alreadyRunning: false, Ct);

        // What we started is ended again; what the start closed is not closed a second time.
        await _apps.Received(1).StopAsync(@"C:\SimHub\SimHubWPF.exe", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _apps.Received(1).StopAsync(@"C:\Other\Nagging.exe", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_WithoutStopApps_LeavesThemRunning()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe" } },
            Exit = new GameExitAction { Kind = GameExitKind.Stay, StopApps = false },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        await Runner().RunAsync(game, alreadyRunning: false, Ct);

        await _apps.DidNotReceive().StopAsync(Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_SwitchesToTheNamedProfileWhenTheGameEnds()
    {
        GameEntry game = Game() with { Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = _desk.Id } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Exit.ShouldBe(SwitchOutcome.Applied);
        await _switcher.Received(1).SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Run_SwitchesBackToThePreviousProfileWhenAskedTo()
    {
        _switcher.ToggleTarget.Returns(_desk);
        GameEntry game = Game() with { Exit = new GameExitAction { Kind = GameExitKind.PreviousProfile } };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        await Runner().RunAsync(game, alreadyRunning: false, Ct);

        await _switcher.Received(1).SwitchAsync(_desk, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>An entry may switch nothing at all – then it is only a launcher, and that has to work too.</summary>
    [Fact]
    public async Task Run_WithoutAProfile_SwitchesNothing()
    {
        GameEntry game = Game() with { ProfileId = null };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.Profile.ShouldBeNull();
        await _switcher.DidNotReceive().SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Wheelbase software has to be up before the game sees the device; SimHub and Crew Chief attach to a session
    /// that already runs and would find nothing if they started first.
    /// </summary>
    [Fact]
    public async Task Run_StartsTheAppsBeforeAndAfterTheGameInThatOrder()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction>
            {
                new() { Kind = AppActionKind.Start, Path = @"C:\Fanatec\FanaLab.exe", When = AppTiming.BeforeGame },
                new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe", When = AppTiming.AfterGame },
                new() { Kind = AppActionKind.Start, Path = @"C:\CrewChief\CrewChiefV4.exe", When = AppTiming.AfterGame },
            },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        await Runner().RunAsync(game, alreadyRunning: false, Ct);

        Received.InOrder(() =>
        {
            _apps.Start(@"C:\Fanatec\FanaLab.exe", null);
            _starter.Start(Arg.Any<GameLaunch>());
            _apps.Start(@"C:\SimHub\SimHubWPF.exe", null);
            _apps.Start(@"C:\CrewChief\CrewChiefV4.exe", null);
        });
    }

    /// <summary>Waiting half a minute for a wheelbase must not hold up Crew Chief once the game already runs.</summary>
    [Fact]
    public async Task Run_OnlyTheAppsBeforeTheGameWaitForTheUsbDevice()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe", When = AppTiming.AfterGame } },
            AppsWaitForUsbDeviceId = "VID_16D0&PID_0D5A",
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Apps.ShouldBe(AppsOutcome.Applied);
        _apps.Received().Start(@"C:\SimHub\SimHubWPF.exe", null);
    }

    /// <summary>
    /// iRacing: hanging the session on the sim would end it on every return to the menu between two races, so it
    /// hangs on the interface instead.
    /// </summary>
    [Fact]
    public async Task Run_ForASimWhoseInterfaceOutlivesIt_WaitsForTheInterface()
    {
        GameEntry game = Game(new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", ProcessName = "iRacingSim64DX11" })
            with
        { EndsWith = SessionEnd.LauncherProcess, LauncherProcessName = "iRacingUI" };
        _starter.Start(Arg.Any<GameLaunch>()).Returns((IRunningGame?)null);
        _processes.Running.Add(new RunningProcess(1, "iRacingUI", null, _time.GetUtcNow()));
        // The session keeps waiting while the interface is up; only its end ends the session.
        _processes.OnIsRunning = call =>
        {
            if (call >= 3)
            {
                _processes.Running.Clear();
            }
        };

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _processes.IsRunningCalls.ShouldBeGreaterThan(1);
    }

    /// <summary>A dashboard has to go down before the wheelbase software it talks to.</summary>
    [Fact]
    public async Task Run_EndsTheAppsInReverseOrder()
    {
        GameEntry game = Game() with
        {
            Apps = new List<AppAction>
            {
                new() { Kind = AppActionKind.Start, Path = @"C:\Fanatec\FanaLab.exe", When = AppTiming.BeforeGame },
                new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe", When = AppTiming.AfterGame },
            },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());
        _apps.IsRunning(Arg.Any<string>()).Returns(false, false, true, true);

        await Runner().RunAsync(game, alreadyRunning: false, Ct);

        Received.InOrder(() =>
        {
            _apps.StopAsync(@"C:\SimHub\SimHubWPF.exe", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            _apps.StopAsync(@"C:\Fanatec\FanaLab.exe", Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        });
    }

    /// <summary>
    /// The tools are dragged into place while the desktop is still there, so the layout goes back before the game
    /// starts – and on the arrangement the profile just applied, which is what makes the saved coordinates valid.
    /// </summary>
    [Fact]
    public async Task Run_PutsTheHelperWindowsBackBeforeStartingTheGame()
    {
        var desktop = new FakeWindowLayout();
        desktop.Windows.Add(new OpenWindow(1, "SimHubWPF", "SimHub 9.4.2", new PixelRect(0, 0, 800, 600), WindowState.Normal));
        GameEntry game = Game() with
        {
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"C:\SimHub\SimHubWPF.exe" } },
            WindowLayout = new WindowLayout
            {
                Windows = new List<WindowPlacement>
                {
                    new() { ProcessName = "SimHubWPF", Title = "SimHub 9.4.2", Bounds = new PixelRect(3840, 0, 4640, 600) },
                },
            },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner(desktop).RunAsync(game, alreadyRunning: false, Ct);

        result.Windows.ShouldBe(new WindowLayoutResult(1, 0, 0));
        desktop.Placed.Single().Bounds.Left.ShouldBe(3840);
        Received.InOrder(() =>
        {
            _apps.Start(@"C:\SimHub\SimHubWPF.exe", null);
            _starter.Start(Arg.Any<GameLaunch>());
        });
    }

    /// <summary>A window that cannot be moved is annoying, not a reason to leave the game unstarted.</summary>
    [Fact]
    public async Task Run_StartsTheGameEvenWhenNoWindowCouldBePlaced()
    {
        var desktop = new FakeWindowLayout();
        GameEntry game = Game() with
        {
            WindowLayout = new WindowLayout
            {
                Windows = new List<WindowPlacement> { new() { ProcessName = "SimHubWPF", Bounds = new PixelRect(0, 0, 800, 600) } },
            },
        };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner(desktop).RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        result.Windows!.Missing.ShouldBe(1);
        _starter.Received(1).Start(Arg.Any<GameLaunch>());
    }

    /// <summary>A profile that was deleted must not stop the game from starting.</summary>
    [Fact]
    public async Task Run_WithAProfileThatIsGone_StartsTheGameAnyway()
    {
        GameEntry game = Game() with { ProfileId = Guid.NewGuid() };
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => LongRunning());

        GameSessionResult result = await Runner().RunAsync(game, alreadyRunning: false, Ct);

        result.Outcome.ShouldBe(GameSessionOutcome.Ended);
        _starter.Received(1).Start(Arg.Any<GameLaunch>());
    }
}
