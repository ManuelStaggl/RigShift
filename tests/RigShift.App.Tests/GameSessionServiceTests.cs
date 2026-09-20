using System.Collections.Concurrent;
using System.IO;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class GameSessionServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly DispatcherThread _ui = new();
    private readonly AppTestHost _host;
    private readonly InMemoryGameStore _store = new();
    private readonly GameCatalog _games;
    private readonly IGameStarter _starter = Substitute.For<IGameStarter>();
    private readonly Processes _processes = new();
    private readonly WatchClock _clock = new();
    private readonly AutoAdvanceTimeProvider _sessionTime = new();
    private readonly ConcurrentQueue<(GameSessionEvent Event, int Thread)> _events = new();
    private Func<GameSessionRunner>? _runnerFactory;
    private GameSessionService? _service;

    public GameSessionServiceTests()
    {
        _host = new AppTestHost(new FakeDisplayConfigurator(DeskActive()));
        _games = new GameCatalog(_store, _host.Catalog, Logger.None);
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => new FakeRunningGame(_sessionTime, TimeSpan.FromHours(1)));
    }

    public void Dispose()
    {
        _ui.Invoke(() => _service?.Dispose());
        _ui.Dispose();
        _host.Dispose();
    }

    private GameSessionRunner Runner() => new(
        _starter, _processes, Substitute.For<IProfileSwitcher>(), _ => null,
        Substitute.For<IAppLauncher>(), _host.Usb, new SwitchOptions(), _sessionTime, Logger.None);

    /// <summary>Created on the dispatcher thread, as the app creates it during startup.</summary>
    private GameSessionService Service()
    {
        _service = _ui.Invoke(() => new GameSessionService(
            _games, _host.Catalog, _processes, () => (_runnerFactory ?? Runner)(), _clock, Logger.None));
        _service.SessionChanged += (_, e) => _events.Enqueue((e, Environment.CurrentManagedThreadId));
        return _service;
    }

    private static GameEntry Game(string exe = @"C:\Games\iRacing\iRacing.exe", bool startWithGame = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = Path.GetFileNameWithoutExtension(exe),
        StartWithGame = startWithGame,
        Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = exe },
    };

    private async Task<GameSessionEvent> EndOfAsync(GameEntry game)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < giveUp)
        {
            foreach ((GameSessionEvent e, _) in _events)
            {
                if (e.GameId == game.Id && !e.IsRunning)
                {
                    return e;
                }
            }

            await Task.Delay(20, Ct);
        }

        throw new TimeoutException("The session of " + game.Name + " did not end.");
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(20, Ct);
        }
    }

    private void Tick()
    {
        _clock.Tick();
        _ui.Drain();
    }

    [Fact]
    public async Task Start_RunsTheSession_AndReportsStartAndEndOnTheUiThread()
    {
        GameSessionService service = Service();
        GameEntry game = Game();

        _ui.Invoke(() => service.Start(game)).ShouldBeTrue();
        GameSessionEvent end = await EndOfAsync(game);

        end.Outcome.ShouldBe(GameSessionOutcome.Ended);
        end.Failed.ShouldBeFalse();
        service.IsRunning(game.Id).ShouldBeFalse();
        service.RunningSince(game.Id).ShouldBeNull();
        _events.Select(e => e.Event.IsRunning).ShouldBe([true, false]);
        _events.ShouldAllBe(e => e.Thread == _ui.ThreadId);
    }

    /// <summary>Pressing Play twice must not switch twice and start two sets of companion apps.</summary>
    [Fact]
    public void Start_WhileTheGameRuns_IgnoresTheSecondStart()
    {
        GameSessionService service = Service();
        GameEntry game = Game();
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => new HeldGame());

        _ui.Invoke(() => service.Start(game)).ShouldBeTrue();
        _ui.Invoke(() => service.Start(game)).ShouldBeFalse();
        _ui.Invoke(() => ((IGamePlayer)service).Play(game, fromLink: true)).ShouldBeFalse();

        service.IsRunning(game.Id).ShouldBeTrue();
        service.RunningSince(game.Id).ShouldNotBeNull();
        _events.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_TwoGames_RunSideBySide()
    {
        GameSessionService service = Service();
        GameEntry first = Game();
        GameEntry second = Game(@"C:\Games\ACC\acc.exe");
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => new HeldGame());

        _ui.Invoke(() => service.Start(first)).ShouldBeTrue();
        _ui.Invoke(() => service.Start(second)).ShouldBeTrue();

        await UntilAsync(() => _starter.ReceivedCalls().Count() == 2, "Both games should have been started.");
        service.IsRunning(first.Id).ShouldBeTrue();
        service.IsRunning(second.Id).ShouldBeTrue();
    }

    [Fact]
    public async Task Start_GameCannotBeStarted_ReportsTheFailureAndIsFreeAgain()
    {
        GameSessionService service = Service();
        GameEntry game = Game();
        _starter.Start(Arg.Any<GameLaunch>()).Returns<IRunningGame?>(_ => throw new InvalidOperationException("no such file"));

        _ui.Invoke(() => service.Start(game));
        GameSessionEvent end = await EndOfAsync(game);

        end.Outcome.ShouldBe(GameSessionOutcome.StartFailed);
        end.Failed.ShouldBeTrue();
        end.Status.ShouldNotBeNullOrWhiteSpace();
        service.IsRunning(game.Id).ShouldBeFalse();
    }

    /// <summary>A session that never got its runner must not leave the game "running" until the app is restarted.</summary>
    [Fact]
    public async Task Start_RunnerCannotBeCreated_ReportsTheFailureAndIsFreeAgain()
    {
        GameSessionService service = Service();
        GameEntry game = Game();
        _runnerFactory = () => throw new InvalidOperationException("service missing");

        _ui.Invoke(() => service.Start(game));
        GameSessionEvent end = await EndOfAsync(game);

        end.Outcome.ShouldBe(GameSessionOutcome.StartFailed);
        service.IsRunning(game.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task Dispose_EndsARunningSession_WithoutCallingItAFailure()
    {
        GameSessionService service = Service();
        GameEntry game = Game();
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => new HeldGame());
        _ui.Invoke(() => service.Start(game));
        await UntilAsync(() => _starter.ReceivedCalls().Any(), "The game should have been started.");

        _ui.Invoke(service.Dispose);
        _service = null;
        GameSessionEvent end = await EndOfAsync(game);

        end.Outcome.ShouldBeNull();
        end.Status.ShouldBeNull();
        end.Failed.ShouldBeFalse();
    }

    /// <summary>Switching the whole machine because RigShift was started while a game was open would be a nasty surprise.</summary>
    [Fact]
    public async Task Watch_GameAlreadyRunningAtStart_IsLeftAlone()
    {
        GameEntry game = Game(startWithGame: true);
        await _games.SaveAsync(game, Ct);
        _processes.Set("iRacing");
        GameSessionService service = Service();

        _ui.Invoke(service.StartWatching);
        Tick();
        Tick();

        service.IsRunning(game.Id).ShouldBeFalse();
        _events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Watch_GameStartedOutside_RunsItsSessionOnce_AndAgainAfterItEnded()
    {
        GameEntry game = Game(startWithGame: true);
        await _games.SaveAsync(game, Ct);
        GameSessionService service = Service();
        _ui.Invoke(service.StartWatching);

        _processes.Set("iracing");
        Tick();
        Tick();

        service.IsRunning(game.Id).ShouldBeTrue();
        _events.Count.ShouldBe(1);
        _starter.DidNotReceive().Start(Arg.Any<GameLaunch>());

        _processes.Set();
        (await EndOfAsync(game)).Outcome.ShouldBe(GameSessionOutcome.Ended);
        Tick();
        _processes.Set("iRacing");
        Tick();

        service.IsRunning(game.Id).ShouldBeTrue();
        _events.Count(e => e.Event.IsRunning).ShouldBe(2);
    }

    [Fact]
    public async Task Watch_GameThatDoesNotAskForIt_IsIgnored()
    {
        GameEntry game = Game(startWithGame: false);
        await _games.SaveAsync(game, Ct);
        GameSessionService service = Service();
        _ui.Invoke(service.StartWatching);

        _processes.Set("iRacing");
        Tick();

        service.IsRunning(game.Id).ShouldBeFalse();
    }

    [Fact]
    public async Task Watch_ProcessListFails_KeepsWatching()
    {
        GameEntry game = Game(startWithGame: true);
        await _games.SaveAsync(game, Ct);
        GameSessionService service = Service();
        _ui.Invoke(service.StartWatching);

        _processes.Fail = true;
        Tick();
        _processes.Fail = false;
        _processes.Set("iRacing");
        Tick();

        service.IsRunning(game.Id).ShouldBeTrue();
    }

    [Fact]
    public void StartWatching_Twice_KeepsOneTimer()
    {
        GameSessionService service = Service();

        _ui.Invoke(service.StartWatching);
        _ui.Invoke(service.StartWatching);

        _clock.TimersCreated.ShouldBe(1);
    }

    /// <summary>A started game that runs until the test lets it go or the session is cancelled.</summary>
    private sealed class HeldGame : IRunningGame
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 4711;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exit.TrySetResult();
    }

    /// <summary>The process list, read by the watcher on the UI thread and by the session on the pool.</summary>
    private sealed class Processes : IGameProcesses
    {
        private readonly Lock _gate = new();
        private List<RunningProcess> _running = [];

        public volatile bool Fail;

        public void Set(params string[] names)
        {
            lock (_gate)
            {
                _running = [.. names.Select((name, index) => new RunningProcess(100 + index, name, null, DateTimeOffset.UnixEpoch))];
            }
        }

        public IReadOnlyList<RunningProcess> List()
        {
            if (Fail)
            {
                throw new InvalidOperationException("access denied");
            }

            lock (_gate)
            {
                return _running;
            }
        }

        public bool IsRunning(string processName) =>
            List().Any(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase));

        public Task WaitForExitAsync(int processId, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Real time, but the watch timer only fires when the test says so.</summary>
    private sealed class WatchClock : TimeProvider
    {
        private TimerCallback? _tick;

        public int TimersCreated { get; private set; }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            TimersCreated++;
            _tick = callback;
            return new Inert();
        }

        public void Tick() => _tick?.Invoke(null);

        private sealed class Inert : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
