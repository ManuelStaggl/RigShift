using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Core.Games;

/// <summary>
/// One session of a game: switch to its profile, bring the companion apps up, start the game, wait for it to end,
/// take the apps down again, run the exit action. Everything before the game itself is the profile machinery we
/// already have – a game session is the bracket around it, which is what no launcher out there does.
/// </summary>
public sealed class GameSessionRunner
{
    private readonly IGameStarter _starter;
    private readonly IGameProcesses _processes;
    private readonly GameProcessLearner _learner;
    private readonly IProfileSwitcher _switcher;
    private readonly Func<Guid, Profile?> _profile;
    private readonly AppRunner _apps;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <param name="profile">Looks a profile up by id; <c>null</c> when it was deleted in the meantime.</param>
    public GameSessionRunner(
        IGameStarter starter,
        IGameProcesses processes,
        IProfileSwitcher switcher,
        Func<Guid, Profile?> profile,
        IAppLauncher apps,
        IUsbDeviceList usbDevices,
        SwitchOptions options,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(switcher);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(usbDevices);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _starter = starter;
        _processes = processes;
        _switcher = switcher;
        _profile = profile;
        _time = time;
        _log = log.ForContext<GameSessionRunner>();
        _learner = new GameProcessLearner(processes, time, _log);
        _apps = new AppRunner(apps, usbDevices, options, time, _log);
    }

    /// <summary>Raised with the learned process name so the caller can keep it in the entry; never raised twice for one session.</summary>
    public event EventHandler<GameProcessLearned>? ProcessLearned;

    /// <summary>
    /// Runs the session and completes when the game has ended and the exit action is done.
    /// </summary>
    /// <param name="alreadyRunning">
    /// The game was started outside RigShift and is already running: the profile is still applied and the apps still
    /// come up, but nothing is started and nothing is learned.
    /// </param>
    public async Task<GameSessionResult> RunAsync(GameEntry game, bool alreadyRunning, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        long started = _time.GetTimestamp();

        SwitchOutcome? applied = await ApplyProfileAsync(game, cancellationToken);
        if (applied is { } outcome && outcome is not (SwitchOutcome.Applied or SwitchOutcome.AppliedPartially))
        {
            // Starting a game into a layout that was not applied is worse than not starting it.
            _log.Warning("Game {Game}: the profile ended as {Outcome}, so the game was not started", game.Name, outcome);
            return new GameSessionResult(GameSessionOutcome.ProfileFailed, outcome, null);
        }

        AppsOutcome apps = await _apps.Start(AppPlan.For(game));

        int? processId = null;
        string? learned = null;
        if (!alreadyRunning)
        {
            IReadOnlySet<int> before = _learner.Snapshot();
            try
            {
                processId = _starter.Start(game.Launch);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Game {Game} could not be started", game.Name);
                return new GameSessionResult(GameSessionOutcome.StartFailed, applied, apps);
            }

            if (processId is null && game.Launch.KnownProcessName() is null)
            {
                RunningProcess? found = await _learner.LearnAsync(before, game.Launch.InstallFolder, cancellationToken);
                if (found is null)
                {
                    _log.Warning("Game {Game}: no process could be recognised, the session ends here", game.Name);
                    return new GameSessionResult(GameSessionOutcome.NotRecognised, applied, apps);
                }

                processId = found.Id;
                learned = found.Name;
                ProcessLearned?.Invoke(this, new GameProcessLearned(game.Id, found.Name));
            }
        }

        await WaitForEndAsync(game, processId, learned, cancellationToken);
        _log.Information("Game {Game} ended after {Minutes:0.0} min", game.Name, _time.GetElapsedTime(started).TotalMinutes);

        await EndAppsAsync(game, cancellationToken);
        SwitchOutcome? exit = await RunExitActionAsync(game, cancellationToken);
        return new GameSessionResult(GameSessionOutcome.Ended, applied, apps, exit, learned);
    }

    private async Task<SwitchOutcome?> ApplyProfileAsync(GameEntry game, CancellationToken cancellationToken)
    {
        if (game.ProfileId is not { } id)
        {
            return null;
        }

        if (_profile(id) is not { } profile)
        {
            _log.Warning("Game {Game} names a profile that no longer exists, switching nothing", game.Name);
            return null;
        }

        SwitchResult? result = await _switcher.SwitchAsync(profile, SwitchRequest.Default, cancellationToken);
        if (result is null)
        {
            _log.Warning("Game {Game}: another switch was running, so nothing was applied", game.Name);
            return SwitchOutcome.Blocked;
        }

        return result.Outcome;
    }

    /// <summary>
    /// Waits for the game to end. With a process id that is exact; otherwise the known name is polled, which is the
    /// case for a game that was already running when we noticed it.
    /// </summary>
    private async Task WaitForEndAsync(GameEntry game, int? processId, string? learned, CancellationToken cancellationToken)
    {
        if (processId is { } id)
        {
            await _processes.WaitForExitAsync(id, cancellationToken);
            return;
        }

        string? name = learned ?? game.Launch.KnownProcessName();
        if (name is null)
        {
            return;
        }

        while (_processes.IsRunning(name))
        {
            await Task.Delay(GameProcessLearner.PollInterval, _time, cancellationToken);
        }
    }

    private async Task EndAppsAsync(GameEntry game, CancellationToken cancellationToken)
    {
        // Whatever still waits for a USB device from the start must not keep running once the game is over.
        await _apps.CancelPendingAsync();
        if (!game.Exit.StopApps)
        {
            return;
        }

        AppPlan stop = AppPlan.For(game).OnlyStopActions();
        if (stop.Apps.Count > 0)
        {
            await _apps.Start(stop).WaitAsync(cancellationToken);
        }
    }

    private async Task<SwitchOutcome?> RunExitActionAsync(GameEntry game, CancellationToken cancellationToken)
    {
        Profile? target = game.Exit.Kind switch
        {
            GameExitKind.PreviousProfile => _switcher.ToggleTarget,
            GameExitKind.Profile when game.Exit.ProfileId is { } id => _profile(id),
            _ => null,
        };

        if (target is null)
        {
            return null;
        }

        _log.Information("Game {Game} ended, switching to {Profile}", game.Name, target.Name);
        SwitchResult? result = await _switcher.SwitchAsync(target, SwitchRequest.Default, cancellationToken);
        return result?.Outcome ?? SwitchOutcome.Blocked;
    }
}

/// <summary>The process name a first start taught us about a game.</summary>
public sealed record GameProcessLearned(Guid GameId, string ProcessName);

/// <summary>Outcome of one game session; the parts are judged separately, as with a switch.</summary>
/// <param name="Outcome">How far the session got.</param>
/// <param name="Profile">What the profile switch did, or <c>null</c> when the entry names none.</param>
/// <param name="Apps">What the companion apps did, or <c>null</c> when the session ended before them.</param>
/// <param name="Exit">What the exit action did, or <c>null</c> for "stay".</param>
/// <param name="LearnedProcessName">The process name this session learned, if any.</param>
public sealed record GameSessionResult(
    GameSessionOutcome Outcome,
    SwitchOutcome? Profile,
    AppsOutcome? Apps,
    SwitchOutcome? Exit = null,
    string? LearnedProcessName = null);

public enum GameSessionOutcome
{
    /// <summary>The game ran and ended; everything else is in the other fields.</summary>
    Ended,

    /// <summary>The profile could not be applied, so the game was not started.</summary>
    ProfileFailed,

    /// <summary>Starting the game failed – a missing executable, a store that is not installed.</summary>
    StartFailed,

    /// <summary>The game was started but no process could be recognised, so its end cannot be noticed either.</summary>
    NotRecognised,
}
