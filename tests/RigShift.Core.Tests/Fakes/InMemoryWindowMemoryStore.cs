using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

/// <summary>Window memory that outlives an orchestrator, like <c>window-memory.json</c> outlives the process.</summary>
internal sealed class InMemoryWindowMemoryStore : IWindowMemoryStore
{
    public Dictionary<Guid, RememberedWindows> Profiles { get; } = [];

    public Task<RememberedWindows?> LoadAsync(Guid profileId, CancellationToken cancellationToken) =>
        Task.FromResult(Profiles.GetValueOrDefault(profileId));

    public Task SaveAsync(Guid profileId, RememberedWindows windows, CancellationToken cancellationToken)
    {
        Profiles[profileId] = windows;
        return Task.CompletedTask;
    }
}
