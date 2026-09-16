using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Core.Games;

/// <summary>
/// Learns which process is the game after a start through a store URI. Such a start hands us the store client, never
/// the game, so without this the entry could neither tell whether the game runs nor notice that it ended.
///
/// The alternative would be a fixed table of URI to process name, as Sherpa keeps one – that works for the games in
/// the table and for no other. Learning works for every game once.
/// </summary>
public sealed class GameProcessLearner(IGameProcesses processes, TimeProvider time, ILogger log)
{
    private readonly IGameProcesses _processes = processes;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log.ForContext<GameProcessLearner>();

    /// <summary>How long to watch for the game to show up before giving up. Store clients and shader caches are slow.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long to keep watching after the first candidate. rFactor 2, Automobilista 2 and DCS put a launcher in
    /// front of the game, so the first new process is regularly the wrong one.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(15);

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>The processes running right now, to be passed to <see cref="LearnAsync"/> as "everything that was already there".</summary>
    public IReadOnlySet<int> Snapshot()
    {
        var ids = new HashSet<int>();
        foreach (RunningProcess process in _processes.List())
        {
            ids.Add(process.Id);
        }

        return ids;
    }

    /// <summary>
    /// Watches for the game's own process after <paramref name="before"/> was taken and the game was started.
    /// </summary>
    /// <param name="before">Process ids from <see cref="Snapshot"/>, taken before the start.</param>
    /// <param name="installFolder">
    /// The game's folder. Processes running from it are the game; everything else is the store client, an overlay or
    /// a helper. Without a folder every new process qualifies, which is why the caller then has to ask the user.
    /// </param>
    /// <param name="ignoreProcessName">
    /// A launcher or interface that is known to sit in front of the game and to run from the same folder, such as
    /// iRacing's. Without this it would be the answer, being both new and the first to start.
    /// </param>
    /// <returns>The game's process, or <c>null</c> when nothing showed up in time.</returns>
    public async Task<RunningProcess?> LearnAsync(
        IReadOnlySet<int> before, string? installFolder, CancellationToken cancellationToken, string? ignoreProcessName = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        DateTimeOffset deadline = _time.GetUtcNow() + Timeout;
        DateTimeOffset? settleUntil = null;
        var candidates = new Dictionary<int, RunningProcess>();

        while (true)
        {
            foreach (RunningProcess process in Candidates(before, installFolder, ignoreProcessName))
            {
                if (candidates.TryAdd(process.Id, process))
                {
                    _log.Debug("Candidate for the game process: {Name} ({ProcessId}) from {Path}", process.Name, process.Id, process.ExecutablePath);
                    settleUntil ??= _time.GetUtcNow() + Settle;
                }
            }

            DateTimeOffset now = _time.GetUtcNow();
            if (settleUntil is { } until && now >= until)
            {
                break;
            }

            if (now >= deadline)
            {
                _log.Information("No game process showed up within {Seconds} s", Timeout.TotalSeconds);
                return null;
            }

            await Task.Delay(PollInterval, _time, cancellationToken);
        }

        // Of the candidates still alive, the one that started first: a pre-launcher is either gone by now or started
        // after the game it launched.
        var alive = new HashSet<int>(_processes.List().Select(p => p.Id));
        RunningProcess? game = candidates.Values
            .Where(p => alive.Contains(p.Id))
            .OrderBy(p => p.StartedAt)
            .ThenBy(p => p.Id)
            .FirstOrDefault();

        if (game is null)
        {
            _log.Information("Every candidate had ended again, nothing learned");
            return null;
        }

        _log.Information("Learned the game process {Name} ({ProcessId})", game.Name, game.Id);
        return game;
    }

    private IEnumerable<RunningProcess> Candidates(IReadOnlySet<int> before, string? installFolder, string? ignoreProcessName)
    {
        foreach (RunningProcess process in _processes.List())
        {
            if (before.Contains(process.Id)
                || string.Equals(process.Name, ignoreProcessName, StringComparison.OrdinalIgnoreCase)
                || !IsFromInstallFolder(process, installFolder))
            {
                continue;
            }

            yield return process;
        }
    }

    /// <summary>
    /// True when the process runs from the game's folder. An unreadable path (an elevated process) counts as well:
    /// dropping it would lose exactly the games that run elevated.
    /// </summary>
    public static bool IsFromInstallFolder(RunningProcess process, string? installFolder)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (string.IsNullOrWhiteSpace(installFolder) || process.ExecutablePath is not { Length: > 0 } path)
        {
            return true;
        }

        try
        {
            string folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installFolder));
            string full = Path.GetFullPath(path);
            return full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }
}
