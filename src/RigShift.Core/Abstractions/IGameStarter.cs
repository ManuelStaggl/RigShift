using RigShift.Core.Games;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for starting a game, either as an executable or through its store's URI.</summary>
public interface IGameStarter
{
    /// <summary>Starts the game. Returns the started process when the start handed one over, which only a direct
    /// executable does – a store URI reaches the client, and the game is its grandchild. The caller disposes it.</summary>
    IRunningGame? Start(GameLaunch launch);
}

/// <summary>
/// A process we started ourselves, held open. Looking it up again by id later would not do: once it has ended Windows
/// hands the id to the next process, and the session would hang on a stranger or take the game for gone.
/// </summary>
public interface IRunningGame : IDisposable
{
    int Id { get; }

    /// <summary>Completes when the process has ended, or right away when it already has.</summary>
    Task WaitForExitAsync(CancellationToken cancellationToken);
}
