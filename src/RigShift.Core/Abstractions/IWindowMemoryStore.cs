using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Where the windows of each profile are kept between switches. State, not configuration: it is rewritten on every
/// switch away from a profile that remembers its windows, so it does not live in the profile file.
/// </summary>
public interface IWindowMemoryStore
{
    /// <summary>The windows remembered for this profile, or <c>null</c> when it was never left.</summary>
    Task<RememberedWindows?> LoadAsync(Guid profileId, CancellationToken cancellationToken);

    Task SaveAsync(Guid profileId, RememberedWindows windows, CancellationToken cancellationToken);
}
