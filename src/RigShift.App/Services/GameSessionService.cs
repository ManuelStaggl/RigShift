using System.Collections.Concurrent;
using System.Windows.Threading;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Runs game sessions and watches for games that were started elsewhere. One session per game at a time – pressing
/// Play twice must not switch the profile twice and start two sets of companion apps.
/// </summary>
public sealed class GameSessionService : IGamePlayer, IDisposable
{
    private readonly GameCatalog _catalog;
    private readonly ProfileCatalog _profiles;
    private readonly IGameProcesses _processes;
    private readonly Func<GameSessionRunner> _runner;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<Guid, RunningSession> _running = new();

    /// <summary>Process names of games seen running, so one start fires the automatic session once, not every five seconds.</summary>
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly Lock _watchGate = new();

    private ITimer? _watch;
    private bool _watching;

    /// <summary>What the timer looks for; replaced as a whole, so a look always belongs to one set.</summary>
    private volatile WatchSet _watchSet = new(NoNames, NoNames);

    /// <summary>The set whose new names already set their starting point; UI thread only.</summary>
    private WatchSet? _baselined;

    /// <summary>How often to look for a game that was started outside RigShift.</summary>
    private static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(5);

    private static readonly IReadOnlySet<string> NoNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>The games whose session runs now; quitting would lose their way back (v4 findings A-14, E-06).</summary>
    public IReadOnlyList<Guid> RunningGames => [.. _running.Keys];

    /// <summary>When the running session of this game started, or <c>null</c> when none runs.</summary>
    public DateTimeOffset? RunningSince(Guid gameId) => _running.TryGetValue(gameId, out RunningSession? session) ? session.StartedAt : null;

    /// <summary>
    /// Starts watching for games that run without RigShift having started them – but only while a game asks for it and
    /// its process name is known; without one, looking at every process every five seconds was the only noticeable load
    /// of an idle RigShift (v4 findings A-05, E-08). Games that already run when watching starts only set the starting
    /// point – switching the whole machine because the app was started while a game was open would be a nasty surprise.
    /// </summary>
    public void StartWatching()
    {
        if (_watching)
        {
            return;
        }

        _watching = true;
        _catalog.Changed += OnGamesChanged;
        UpdateWatch();
    }

    private void OnGamesChanged(object? sender, EventArgs e) => UpdateWatch();

    /// <summary>Starts or stops the timer as the games ask for it.</summary>
    private void UpdateWatch()
    {
        var watched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (GameEntry game in _catalog.Games)
        {
            if (game.StartWithGame && game.Launch.KnownProcessName() is { } name)
            {
                watched.Add(name);
            }
        }

        lock (_watchGate)
        {
            WatchSet current = _watchSet;
            if (_stopping.IsCancellationRequested || watched.SetEquals(current.Names))
            {
                return;
            }

            _watchSet = new WatchSet(watched, watched.Where(name => !current.Names.Contains(name)).ToHashSet(StringComparer.OrdinalIgnoreCase));
            if (watched.Count == 0)
            {
                _watch?.Dispose();
                _watch = null;
                _log.Information("Not watching for games started outside RigShift: no game asks for it");
            }
            else if (_watch is null)
            {
                _log.Information("Watching for {Count} game(s) started outside RigShift", watched.Count);
                _watch = _time.CreateTimer(_ => Look(), null, TimeSpan.Zero, WatchInterval);
            }
        }
    }

    /// <summary>On the timer's thread: the process list stays off the UI thread, only the answer goes there.</summary>
    private void Look()
    {
        WatchSet set = _watchSet;
        if (_stopping.IsCancellationRequested || set.Names.Count == 0)
        {
            return;
        }

        IReadOnlySet<string> running;
        try
        {
            running = _processes.FindRunning(set.Names);
        }
        catch (Exception ex)
        {
            // Nothing is decided on a failed look: taking it for "nothing runs" would start a running game's session again.
            _log.Warning(ex, "Running programs could not be listed");
            return;
        }

        _dispatcher.BeginInvoke(() => CheckForStartedGames(set, running));
    }

    /// <summary>
    /// Runs the game. Does nothing when a session for it is already running – pressing Play twice must not switch
    /// twice.
    /// </summary>
    /// <returns><c>false</c> when a session for it was already running, so nothing was started.</returns>
    public bool Start(GameEntry game, bool alreadyRunning = false, bool fromLink = false)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (_running.ContainsKey(game.Id))
        {
            _log.Information("Game {Game} is already running, ignoring the second start", game.Name);
            return false;
        }

        var session = new RunningSession(_time.GetUtcNow());
        if (!_running.TryAdd(game.Id, session))
        {
            return false;
        }

        session.Task = RunAsync(game, alreadyRunning, fromLink);
        Raise(game, running: true, status: null, outcome: null);
        return true;
    }

    /// <summary>The command line and the tray menu start a game through here.</summary>
    bool IGamePlayer.Play(GameEntry game, bool fromLink) => Start(game, fromLink: fromLink);

    private async Task RunAsync(GameEntry game, bool alreadyRunning, bool fromLink)
    {
        // Inside the try: a runner that cannot be created must end the session too, or the game stays "running".
        GameSessionRunner? runner = null;
        try
        {
            runner = _runner();
            runner.ProcessLearned += OnProcessLearned;
            GameSessionResult result = await Task.Run(
                () => runner.RunAsync(game, alreadyRunning, fromLink, _stopping.Token), CancellationToken.None);
            _log.Information("Game {Game} finished as {Outcome}", game.Name, result.Outcome);
            await _dispatcher.InvokeAsync(() => Finish(game, GameMessages.Describe(result), result.Outcome));
        }
        catch (OperationCanceledException)
        {
            await _dispatcher.InvokeAsync(() => Finish(game, null, null));
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Game {Game} failed", game.Name);
            await _dispatcher.InvokeAsync(() => Finish(game, UserMessages.Describe(ex), GameSessionOutcome.StartFailed));
        }
        finally
        {
            runner?.ProcessLearned -= OnProcessLearned;
        }
    }

    private void OnProcessLearned(object? sender, GameProcessLearned learned) =>
        _dispatcher.BeginInvoke(() => _catalog.RememberProcessNameAsync(learned.GameId, learned.ProcessName, CancellationToken.None));

    private void Finish(GameEntry game, string? status, GameSessionOutcome? outcome)
    {
        _running.TryRemove(game.Id, out _);

        // The session ended, so the game's process is gone; forget it, or the watcher would never fire for it again.
        if (game.Launch.KnownProcessName() is { } name)
        {
            _seen.Remove(name);
        }

        Raise(game, running: false, status, outcome);
        _ = _profiles.RefreshActiveAsync(CancellationToken.None);
    }

    /// <summary>
    /// Looks for a game whose process turned up without us starting it. Only games that ask for it
    /// (<see cref="GameEntry.StartWithGame"/>) and whose process name is known – which the first start learns.
    /// </summary>
    private void CheckForStartedGames(WatchSet set, IReadOnlySet<string> running)
    {
        // A look for names that changed meanwhile decides nothing; the next one belongs to the new set.
        if (_stopping.IsCancellationRequested || !ReferenceEquals(set, _watchSet))
        {
            return;
        }

        if (!ReferenceEquals(set, _baselined))
        {
            // A game that runs already when watching for it begins only sets the starting point.
            _baselined = set;
            List<string> already = [.. set.Added.Where(running.Contains)];
            _seen.UnionWith(already);
            _log.Information("{Count} watched game(s) already running, left alone", already.Count);
        }

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

    private void Raise(GameEntry game, bool running, string? status, GameSessionOutcome? outcome) =>
        SessionChanged?.Invoke(this, new GameSessionEvent(game.Id, running, status, outcome, _time.GetUtcNow()));

    /// <param name="Names">The process names of the games that start their session when started elsewhere.</param>
    /// <param name="Added">Those not in the set before: when they already run, they only set the starting point.</param>
    private sealed record WatchSet(IReadOnlySet<string> Names, IReadOnlySet<string> Added);

    private sealed class RunningSession(DateTimeOffset startedAt)
    {
        public DateTimeOffset StartedAt { get; } = startedAt;

        public Task? Task { get; set; }
    }

    public void Dispose()
    {
        _catalog.Changed -= OnGamesChanged;
        lock (_watchGate)
        {
            _stopping.Cancel();
            _watch?.Dispose();
            _watch = null;
        }

        _stopping.Dispose();
    }
}

/// <summary>A session started or ended.</summary>
/// <param name="GameId">The game it belongs to.</param>
/// <param name="IsRunning">Whether the session runs now.</param>
/// <param name="Status">What to show on the card, or <c>null</c> for nothing.</param>
/// <param name="Outcome">How the session ended; <c>null</c> while it runs or when RigShift itself stopped it.</param>
/// <param name="At">When it happened.</param>
public sealed record GameSessionEvent(Guid GameId, bool IsRunning, string? Status, GameSessionOutcome? Outcome, DateTimeOffset At)
{
    /// <summary>The session ended without the game having run to its end (profile, start or recognition failed).</summary>
    public bool Failed => Outcome is { } outcome && outcome != GameSessionOutcome.Ended;
}

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
