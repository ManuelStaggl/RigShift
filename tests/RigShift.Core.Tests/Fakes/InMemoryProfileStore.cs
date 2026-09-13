using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

internal sealed class InMemoryProfileStore : IProfileStore
{
    public List<Profile> Profiles { get; } = [];

    public Task<IReadOnlyList<Profile>> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Profile>>(Profiles.ToList());

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
