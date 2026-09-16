using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// A process list the test drives: <see cref="Running"/> is what <see cref="List"/> returns, and
/// <see cref="OnListed"/> runs before each call so a test can let a process appear or end between two polls.
/// </summary>
internal sealed class FakeGameProcesses : IGameProcesses
{
    private readonly List<RunningProcess> _running = [];

    public List<RunningProcess> Running => _running;

    /// <summary>Called with the number of <see cref="List"/> calls so far, before the list is returned.</summary>
    public Action<int>? OnListed { get; set; }

    public int ListCalls { get; private set; }

    public IReadOnlyList<RunningProcess> List()
    {
        ListCalls++;
        OnListed?.Invoke(ListCalls);
        return [.. _running];
    }

    /// <summary>Called with the number of <see cref="IsRunning"/> calls so far, before the answer is given.</summary>
    public Action<int>? OnIsRunning { get; set; }

    public int IsRunningCalls { get; private set; }

    public Task WaitForExitAsync(int processId, CancellationToken cancellationToken) => Task.CompletedTask;

    public bool IsRunning(string processName)
    {
        IsRunningCalls++;
        OnIsRunning?.Invoke(IsRunningCalls);
        return _running.Exists(p => string.Equals(p.Name, processName, StringComparison.OrdinalIgnoreCase));
    }
}
