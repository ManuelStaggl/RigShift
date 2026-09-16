using RigShift.Core.Games;

namespace RigShift.Core.Abstractions;

/// <summary>Persistence for game entries. Implementation: <c>JsonGameStore</c>, one file %AppData%\RigShift\games.json.</summary>
public interface IGameStore
{
    Task<GameLoadResult> LoadAllAsync(CancellationToken cancellationToken);

    /// <summary>Adds or replaces the entry and writes the whole file.</summary>
    Task SaveAsync(GameEntry game, CancellationToken cancellationToken);

    Task DeleteAsync(Guid gameId, CancellationToken cancellationToken);
}

/// <summary>
/// The games that could be loaded, plus the reason the file could not be read – so callers never mistake an empty
/// list for "no games configured".
/// </summary>
public sealed record GameLoadResult(IReadOnlyList<GameEntry> Games, string? Unreadable)
{
    public static GameLoadResult Empty { get; } = new([], null);

    public bool IsComplete => Unreadable is null;
}
