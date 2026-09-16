using RigShift.Core.Abstractions;
using RigShift.Core.Games;

namespace RigShift.Core.Tests.Fakes;

internal sealed class InMemoryGameStore : IGameStore
{
    public List<GameEntry> Games { get; } = [];

    /// <summary>Reason the fake reports the file as unreadable; <c>null</c> for a readable one.</summary>
    public string? Unreadable { get; set; }

    public Task<GameLoadResult> LoadAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new GameLoadResult(Games.ToList(), Unreadable));

    public Task SaveAsync(GameEntry game, CancellationToken cancellationToken)
    {
        int index = Games.FindIndex(g => g.Id == game.Id);
        if (index >= 0)
        {
            Games[index] = game;
        }
        else
        {
            Games.Add(game);
        }

        return Task.CompletedTask;
    }

    public Task DeleteAsync(Guid gameId, CancellationToken cancellationToken)
    {
        Games.RemoveAll(g => g.Id == gameId);
        return Task.CompletedTask;
    }
}
