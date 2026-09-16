using RigShift.Core.Games;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for starting a game, either as an executable or through its store's URI.</summary>
public interface IGameStarter
{
    /// <summary>Starts the game. Returns the game's process id when the start handed one over, which only a direct
    /// executable does – a store URI reaches the client, and the game is its grandchild.</summary>
    int? Start(GameLaunch launch);
}
