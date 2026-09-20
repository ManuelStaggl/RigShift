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
    private readonly WindowLayoutRestorer? _layout;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <param name="profile">Looks a profile up by id; <c>null</c> when it was deleted in the meantime.</param>
    /// <param name="windows">Restores saved window positions; <c>null</c> leaves windows where they are.</param>
    public GameSessionRunner(
        IGameStarter starter,
        IGameProcesses processes,
        IProfileSwitcher switcher,
        Func<Guid, Profile?> profile,
        IAppLauncher apps,
        IUsbDeviceList usbDevices,
        SwitchOptions options,
        TimeProvider time,
        ILogger log,
        IWindowLayout? windows = null)
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
        _layout = windows is null ? null : new WindowLayoutRestorer(windows, time, _log);
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
    public Task<GameSessionResult> RunAsync(GameEntry game, bool alreadyRunning, CancellationToken cancellationToken) =>
        RunAsync(game, alreadyRunning, fromLink: false, cancellationToken);

    /// <param name="fromLink">
    /// The session was asked for by a <c>rigshift://play</c> link: the switch asks for confirmation even when that is
    /// turned off, and a "no" ends the session before anything is started.
    /// </param>
    /// <inheritdoc cref="RunAsync(GameEntry, bool, CancellationToken)"/>
    public async Task<GameSessionResult> RunAsync(GameEntry game, bool alreadyRunning, bool fromLink, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        long started = _time.GetTimestamp();

        SwitchOutcome? applied = await ApplyProfileAsync(game, fromLink, cancellationToken);
        if (applied is { } outcome && outcome is not (SwitchOutcome.Applied or SwitchOutcome.AppliedPartially))
        {
            // Starting a game into a layout that was not applied is worse than not starting it.
            _log.Warning("Game {Game}: the profile ended as {Outcome}, so the game was not started", game.Name, outcome);
            return new GameSessionResult(GameSessionOutcome.ProfileFailed, outcome, null);
        }

        // Wheelbase software, Trading Paints and anything else the game must already see when it comes up.
        AppsOutcome apps = await _apps.Start(AppPlan.For(game, AppTiming.BeforeGame));

        // Before the game, not after: the tools are dragged into place while the desktop is still visible, and the
        // arrangement they are placed on is the one the profile just applied.
        WindowLayoutResult? layout = await RestoreWindowsAsync(game, cancellationToken);

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
                return new GameSessionResult(GameSessionOutcome.StartFailed, applied, apps, Windows: layout);
            }

            if (processId is null && game.Launch.KnownProcessName() is null)
            {
                RunningProcess? found = await _learner.LearnAsync(
                    before, game.Launch.InstallFolder, cancellationToken, game.LauncherProcessName);
                if (found is null)
                {
                    _log.Warning("Game {Game}: no process could be recognised, the session ends here", game.Name);
                    return new GameSessionResult(GameSessionOutcome.NotRecognised, applied, apps, Windows: layout);
                }

                processId = found.Id;
                learned = found.Name;
                ProcessLearned?.Invoke(this, new GameProcessLearned(game.Id, found.Name));
            }
        }

        // SimHub, Crew Chief and the overlays attach to a session that already runs, so they come after the game.
        AppsOutcome afterwards = await _apps.Start(AppPlan.For(game, AppTiming.AfterGame));
        apps = Worse(apps, afterwards);

        await WaitForEndAsync(game, processId, learned, cancellationToken);
        _log.Information("Game {Game} ended after {Minutes:0.0} min", game.Name, _time.GetElapsedTime(started).TotalMinutes);

        await EndAppsAsync(game, cancellationToken);
        SwitchOutcome? exit = await RunExitActionAsync(game, cancellationToken);
        return new GameSessionResult(GameSessionOutcome.Ended, applied, apps, exit, learned, layout);
    }

    /// <summary>
    /// Puts the helper windows back, if the entry saved any. Never fails the session: a window that could not be
    /// moved is annoying, not a reason to leave the game unstarted.
    /// </summary>
    private async Task<WindowLayoutResult?> RestoreWindowsAsync(GameEntry game, CancellationToken cancellationToken)
    {
        if (_layout is null || game.WindowLayout is not { IsEmpty: false } layout)
        {
            return null;
        }

        try
        {
            return await _layout.RestoreAsync(layout, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Game {Game}: the window positions could not be restored", game.Name);
            return null;
        }
    }

    private async Task<SwitchOutcome?> ApplyProfileAsync(GameEntry game, bool fromLink, CancellationToken cancellationToken)
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

        SwitchRequest request = fromLink ? new SwitchRequest { FromLink = true } : SwitchRequest.Default;
        SwitchResult? result = await _switcher.SwitchAsync(profile, request, cancellationToken);
        if (result is null)
        {
            _log.Warning("Game {Game}: another switch was running, so nothing was applied", game.Name);
            return SwitchOutcome.Blocked;
        }

        return result.Outcome;
    }

    /// <summary>
    /// Waits for the session to end. For a sim whose interface outlives it – iRacing, Assetto Corsa with Content
    /// Manager – that is the launcher's process, not the sim's: hanging on the sim would end the session on every
    /// return to the menu between two races. Otherwise the game's own process decides, exactly where a process id
    /// is at hand and by polling the name where it is not.
    /// </summary>
    private async Task WaitForEndAsync(GameEntry game, int? processId, string? learned, CancellationToken cancellationToken)
    {
        if (game.EndsWith == SessionEnd.LauncherProcess && game.LauncherProcessName is { Length: > 0 } launcher)
        {
            _log.Information("Game {Game}: the session ends when {Launcher} does", game.Name, launcher);
            await PollUntilGoneAsync(launcher, cancellationToken);
            return;
        }

        if (processId is { } id)
        {
            await _processes.WaitForExitAsync(id, cancellationToken);
            return;
        }

        if ((learned ?? game.Launch.KnownProcessName()) is { } name)
        {
            await PollUntilGoneAsync(name, cancellationToken);
        }
    }

    private async Task PollUntilGoneAsync(string processName, CancellationToken cancellationToken)
    {
        while (_processes.IsRunning(processName))
        {
            await Task.Delay(GameProcessLearner.PollInterval, _time, cancellationToken);
        }
    }

    /// <summary>The less good of two app outcomes, so one failing group is not hidden by the other succeeding.</summary>
    private static AppsOutcome Worse(AppsOutcome first, AppsOutcome second)
    {
        static int Rank(AppsOutcome outcome) => outcome switch
        {
            AppsOutcome.NotConfigured => 0,
            AppsOutcome.Applied => 1,
            AppsOutcome.Incomplete => 2,
            AppsOutcome.DeviceMissing => 3,
            AppsOutcome.Cancelled => 4,
            _ => 5,
        };

        return Rank(second) > Rank(first) ? second : first;
    }

    private async Task EndAppsAsync(GameEntry game, CancellationToken cancellationToken)
    {
        // Whatever still waits for a USB device from the start must not keep running once the game is over.
        await _apps.CancelPendingAsync();
        if (!game.Exit.StopApps)
        {
            return;
        }

        AppPlan stop = AppPlan.StopWhatWasStarted(game);
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
/// <param name="Windows">What became of the saved window positions, or <c>null</c> when the entry has none.</param>
public sealed record GameSessionResult(
    GameSessionOutcome Outcome,
    SwitchOutcome? Profile,
    AppsOutcome? Apps,
    SwitchOutcome? Exit = null,
    string? LearnedProcessName = null,
    WindowLayoutResult? Windows = null);

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
