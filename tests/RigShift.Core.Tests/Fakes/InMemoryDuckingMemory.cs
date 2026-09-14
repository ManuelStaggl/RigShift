using RigShift.Core.Abstractions;

namespace RigShift.Core.Tests.Fakes;

/// <summary>Ducking memory that outlives an orchestrator, like <c>settings.json</c> outlives the process.</summary>
internal sealed class InMemoryDuckingMemory : IDuckingMemory
{
    public RememberedDucking? Remembered { get; set; }

    public Task<RememberedDucking?> LoadAsync(CancellationToken cancellationToken) => Task.FromResult(Remembered);

    public Task SaveAsync(int? value, CancellationToken cancellationToken)
    {
        Remembered = new RememberedDucking(value);
        return Task.CompletedTask;
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        Remembered = null;
        return Task.CompletedTask;
    }
}
