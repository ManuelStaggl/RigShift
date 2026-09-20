using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.Core.Storage;

/// <summary>
/// What the JSON parser cannot promise: <c>required</c> only says a key is there, and <c>"displays": null</c> satisfies
/// it. Everything read from a file – a profile folder, the game list, a backup – passes here before anything else sees
/// it, so a hand-edited or damaged file ends as "could not be read" and not as a NullReferenceException in the planner.
/// </summary>
public static class StoredDataCheck
{
    /// <summary>The first thing wrong with <paramref name="profile"/>, or <c>null</c> when it can be used.</summary>
    public static string? Problem(Profile? profile)
    {
        if (profile is null)
        {
            return "The file contains no profile.";
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            return "The profile has no name.";
        }

        if (profile.Displays is null || profile.Displays.Any(d => d?.Identity is not { AdapterDevicePath: not null, TargetDevicePath: not null, FriendlyName: not null }))
        {
            return "The profile's displays are incomplete.";
        }

        if (profile.Audio is not { } audio
            || new[] { audio.Playback, audio.PlaybackCommunications, audio.Recording, audio.RecordingCommunications }
                .Any(e => e is { EndpointId: null } or { FriendlyName: null }))
        {
            return "The profile's audio devices are incomplete.";
        }

        if (AppsProblem(profile.Apps) is { } apps)
        {
            return apps;
        }

        if (profile.DesktopIcons is { } icons && (icons.Icons is null || icons.Icons.Any(i => i?.Item is null)))
        {
            return "The profile's desktop symbols are incomplete.";
        }

        if (profile.Surround is { Grid: { } grid } && (grid.Displays is null || grid.Displays.Any(d => d is null)))
        {
            return "The profile's Surround grid is incomplete.";
        }

        return null;
    }

    /// <summary>The first thing wrong with <paramref name="game"/>, or <c>null</c> when it can be used.</summary>
    public static string? Problem(GameEntry? game)
    {
        if (game is null || string.IsNullOrWhiteSpace(game.Name))
        {
            return "The list contains a game without a name.";
        }

        if (game.Launch?.Target is null)
        {
            return $"Game '{game.Name}' has nothing to start.";
        }

        if (game.Exit is null || game.WindowLayout is { } layout && (layout.Windows is null || layout.Windows.Any(w => w?.ProcessName is null)))
        {
            return $"Game '{game.Name}' is incomplete.";
        }

        return AppsProblem(game.Apps);
    }

    private static string? AppsProblem(IReadOnlyList<AppAction>? apps) =>
        apps is null || apps.Any(a => a?.Path is null) ? "The list of programs is incomplete." : null;
}
