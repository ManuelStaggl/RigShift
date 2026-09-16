using System.Collections.Concurrent;
using System.Windows.Threading;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Runs game sessions and watches for games that were started elsewhere. One session per game at a time – pressing
/// Play twice must not switch the profile twice and start two sets of companion apps.
/// </summary>
public sealed class GameSessionService : IDisposable
{
    private readonly GameCatalog _catalog;
    private readonly ProfileCatalog _profiles;
    private readonly IGameProcesses _processes;
    private readonly Func<GameSessionRunner> _runner;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, Task> _running = new();

    /// <summary>Process names of games seen running, so one start fires the automatic session once, not every five seconds.</summary>
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    private ITimer? _watch;

    /// <summary>How often to look for a game that was started outside RigShift.</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    public GameSessionService(
        GameCatalog catalog,
        ProfileCatalog profiles,
        IGameProcesses processes,
        Func<GameSessionRunner> runner,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(processes);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _profiles = profiles;
        _processes = processes;
        _runner = runner;
        _time = time;
        _log = log.ForContext<GameSessionService>();
    }

    /// <summary>Raised on the UI thread when a session started or ended, so the cards can follow.</summary>
    public event EventHandler<GameSessionEvent>? SessionChanged;

    public bool IsRunning(Guid gameId) => _running.ContainsKey(gameId);

    /// <summary>
    /// Starts watching for games that run without RigShift having started them. Games that already run when this is
    /// called only set the starting point – switching the whole machine because the app was started while a game was
    /// open would be a nasty surprise.
    /// </summary>
    public void StartWatching()
    {
        if (_watch is not null)
        {
            return;
        }

        foreach (string name in RunningGameNames())
        {
            _seen.Add(name);
        }

        _log.Information("Watching for games started outside RigShift ({Count} already running)", _seen.Count);
        _watch = _time.CreateTimer(_ => _dispatcher.BeginInvoke(CheckForStartedGames), null, WatchInterval, WatchInterval);
    }

    /// <summary>
    /// Runs the game. Does nothing when a session for it is already running – pressing Play twice must not switch
    /// twice.
    /// </summary>
    public void Start(GameEntry game, bool alreadyRunning = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_running.ContainsKey(game.Id))
        {
            _log.Information("Game {Game} is already running, ignoring the second start", game.Name);
            return;
        }

        Task session = RunAsync(game, alreadyRunning);
        if (!_running.TryAdd(game.Id, session))
        {
            return;
        }

        Raise(game, running: true, status: null);
    }

    private async Task RunAsync(GameEntry game, bool alreadyRunning)
    {
        GameSessionRunner runner = _runner();
        runner.ProcessLearned += OnProcessLearned;
        try
        {
            GameSessionResult result = await Task.Run(
                () => runner.RunAsync(game, alreadyRunning, _stopping.Token), CancellationToken.None);
            _log.Information("Game {Game} finished as {Outcome}", game.Name, result.Outcome);
            await _dispatcher.InvokeAsync(() => Finish(game, GameMessages.Describe(result)));
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() => Finish(game, null));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Game {Game} failed", game.Name);
            await _dispatcher.InvokeAsync(() => Finish(game, ex.Message));
        }
        finally
        {
            runner.ProcessLearned -= OnProcessLearned;
        }
    }

    private void OnProcessLearned(object? sender, GameProcessLearned learned) =>
        _dispatcher.BeginInvoke(() => _catalog.RememberProcessNameAsync(learned.GameId, learned.ProcessName, CancellationToken.None));

    private void Finish(GameEntry game, string? status)
    {
        _running.TryRemove(game.Id, out _);

        // The session ended, so the game's process is gone; forget it, or the watcher would never fire for it again.
        if (game.Launch.KnownProcessName() is { } name)
        {
            _seen.Remove(name);
        }

        Raise(game, running: false, status);
        _ = _profiles.RefreshActiveAsync(CancellationToken.None);
    }

    /// <summary>
    /// Looks for a game whose process turned up without us starting it. Only games that ask for it
    /// (<see cref="GameEntry.StartWithGame"/>) and whose process name is known – which the first start learns.
    /// </summary>
    private void CheckForStartedGames()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        HashSet<string> running = RunningGameNames();
        foreach (GameEntry game in _catalog.Games)
        {
            if (!game.StartWithGame || game.Launch.KnownProcessName() is not { } name)
            {
                continue;
            }

            if (running.Contains(name) && _seen.Add(name) && !_running.ContainsKey(game.Id))
            {
                _log.Information("Game {Game} was started outside RigShift, running its session", game.Name);
                Start(game, alreadyRunning: true);
            }
        }

        _seen.RemoveWhere(name => !running.Contains(name));
    }

    private HashSet<string> RunningGameNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (RunningProcess process in _processes.List())
            {
                names.Add(process.Name);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Running programs could not be listed");
        }

        return names;
    }

    private void Raise(GameEntry game, bool running, string? status) =>
        SessionChanged?.Invoke(this, new GameSessionEvent(game.Id, running, status));

    public void Dispose()
    {
        _watch?.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
    }
}

/// <summary>A session started or ended.</summary>
/// <param name="GameId">The game it belongs to.</param>
/// <param name="IsRunning">Whether the session runs now.</param>
/// <param name="Status">What to show on the card, or <c>null</c> for nothing.</param>
public sealed record GameSessionEvent(Guid GameId, bool IsRunning, string? Status);

/// <summary>User-facing texts for game session results.</summary>
public static class GameMessages
{
    public static string Describe(GameSessionResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        string text = Localization.Loc.Instance["GameOutcome_" + result.Outcome];
        if (result.Profile is SwitchOutcome.Blocked or SwitchOutcome.Failed or SwitchOutcome.RolledBack)
        {
            text += " · " + SwitchMessages.Outcome(result.Profile.Value);
        }

        if (result.Apps is AppsOutcome.Incomplete)
        {
            text += " · " + Localization.Loc.Instance["Result_AppsIncomplete"];
        }

        if (result.Windows is { IsComplete: false } windows)
        {
            text += " · " + Localization.Loc.Format("Game_WindowsIncomplete", windows.Placed, windows.Refused + windows.Missing);
        }

        return text;
    }
}
