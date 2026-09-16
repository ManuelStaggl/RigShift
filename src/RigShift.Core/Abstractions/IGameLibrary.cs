using RigShift.Core.Games;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Installed games found on this machine, so the user picks a game instead of browsing for an executable. Read-only,
/// purely local: everything needed is on disk (Steam's manifests, Epic's manifests), so there is no sign-in, no
/// token and no account – unlike a library manager, we only care about what is installed.
/// </summary>
public interface IGameLibrary
{
    /// <summary>Every installed game the known sources report, newest source order, deduplicated by name and target.</summary>
    IReadOnlyList<InstalledGame> Find();
}

/// <summary>A game found on disk, ready to become a <see cref="GameEntry"/>.</summary>
/// <param name="Name">Title as the store names it.</param>
/// <param name="Launch">How it would be started, including the install folder used while learning the process name.</param>
public sealed record InstalledGame(string Name, GameLaunch Launch)
{
    /// <summary>Short source label for the picker ("Steam", "Epic").</summary>
    public string Source => Launch.Kind switch
    {
        GameLaunchKind.Steam => "Steam",
        GameLaunchKind.Epic => "Epic",
        _ => string.Empty,
    };
}
