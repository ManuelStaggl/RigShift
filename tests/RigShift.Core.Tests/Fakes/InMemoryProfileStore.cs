using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

internal sealed class InMemoryProfileStore : IProfileStore
{
    public List<Profile> Profiles { get; } = [];

    /// <summary>Files the fake reports as unreadable, e.g. to simulate a locked profile file.</summary>
    public List<UnreadableProfileFile> Unreadable { get; } = [];

    public Task<LoadResult> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new LoadResult(Profiles.ToList(), Unreadable.ToList()));

    public Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    {
        int index = Profiles.FindIndex(p => p.Id == profile.Id);
        if (index >= 0)
        {
            Profiles[index] = profile;
        }
        else
        {
            Profiles.Add(profile);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        Profiles.RemoveAll(p => p.Id == profileId);
        return Task.CompletedTask;
    }
}
