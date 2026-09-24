using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>What a game editor starts from besides the game: read from the catalogs and the machine when it opens.</summary>
/// <param name="Profiles">The profiles to choose from; their hotkeys count as taken.</param>
/// <param name="Games">Every configured game, this one included; names and hotkeys are checked against the others.</param>
/// <param name="AppsWaitDevice">The device the tools wait for, with the devices to choose from.</param>
public sealed record GameEditorContext(
    IReadOnlyList<Profile> Profiles,
    IReadOnlyList<GameEntry> Games,
    AppsWaitDeviceChoice AppsWaitDevice);
