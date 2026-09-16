using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>A switch journal in a field, plus a record of what happened to it, so tests can check both.</summary>
internal sealed class InMemorySwitchJournal : ISwitchJournal
{
    public InterruptedSwitch? Entry { get; private set; }

    /// <summary>Every entry that was written, including ones a later <see cref="ClearAsync"/> removed again.</summary>
    public List<InterruptedSwitch> Written { get; } = [];

    public int Cleared { get; private set; }

    public Task BeginAsync(InterruptedSwitch entry, CancellationToken cancellationToken)
    {
        Entry = entry;
        Written.Add(entry);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Entry = null;
        Cleared++;
        return Task.CompletedTask;
    }

    public Task<InterruptedSwitch?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult(Entry);
}
