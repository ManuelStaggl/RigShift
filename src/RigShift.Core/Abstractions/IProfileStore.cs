using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>Persistence for profiles and settings. Windows implementation: JSON files under %LocalAppData%\RigShift.</summary>
public interface IProfileStore
{
    Task<IReadOnlyList<Profile>> LoadAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(Profile profile, CancellationToken cancellationToken);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}
