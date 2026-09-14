using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>Persistence for profiles. Implementation: <c>JsonProfileStore</c>, JSON files under %AppData%\RigShift\profiles.</summary>
public interface IProfileStore
{
    Task<LoadResult> LoadAllAsync(CancellationToken cancellationToken);

    Task SaveAsync(Profile profile, CancellationToken cancellationToken);

    Task DeleteAsync(Guid profileId, CancellationToken cancellationToken);
}

/// <summary>A profile file that was skipped while loading, e.g. locked by another program, broken or from a newer version.</summary>
public sealed record UnreadableProfileFile(string FileName, string Reason);

/// <summary>The profiles that could be loaded, and the files that could not – so callers never mistake a partial list for all profiles.</summary>
public sealed record LoadResult(IReadOnlyList<Profile> Profiles, IReadOnlyList<UnreadableProfileFile> Unreadable)
{
    public static LoadResult Empty { get; } = new([], []);

    /// <summary><c>true</c> if every profile file was read.</summary>
    public bool IsComplete => Unreadable.Count == 0;
}
