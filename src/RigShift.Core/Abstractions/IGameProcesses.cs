namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for watching processes while a game runs. Read-only on purpose: RigShift never ends a game – that
/// would risk unsaved progress, and anti-cheat drivers take a dim view of anyone touching their process.
/// </summary>
public interface IGameProcesses
{
    /// <summary>Every process that could be a game: it has a main window. Never throws for a single unreadable process.</summary>
    IReadOnlyList<RunningProcess> List();

    /// <summary>
    /// Which of <paramref name="names"/> run now with a main window, compared the way the set compares. Cheap enough for a
    /// timer: only a process whose name is asked for has its windows looked at (v4 finding A-05).
    /// </summary>
    IReadOnlySet<string> FindRunning(IReadOnlySet<string> names);

    /// <summary>Completes when the process has ended, or right away when it is already gone.</summary>
    Task WaitForExitAsync(int processId, CancellationToken cancellationToken);

    /// <summary>True while a process with that name runs; the fallback when the id is no longer usable.</summary>
    bool IsRunning(string processName);
}

/// <summary>A running process, as much of it as can be read without elevation.</summary>
/// <param name="Id">Process id; only valid while it runs.</param>
/// <param name="Name">Process name without extension, as Task Manager shows it.</param>
/// <param name="ExecutablePath">
/// Full path of the executable, or <c>null</c> when it cannot be read – which is the normal answer for a process
/// running elevated while RigShift does not.
/// </param>
/// <param name="StartedAt">When the process started.</param>
public sealed record RunningProcess(int Id, string Name, string? ExecutablePath, DateTimeOffset StartedAt);
