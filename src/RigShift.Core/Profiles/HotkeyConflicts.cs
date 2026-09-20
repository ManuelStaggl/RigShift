using RigShift.Core.Games;

namespace RigShift.Core.Profiles;

/// <summary>What inside RigShift a key combination belongs to.</summary>
public enum HotkeyUseKind
{
    Profile,
    Game,

    /// <summary>The "back to the previous profile" hotkey from the settings.</summary>
    Toggle,
}

/// <param name="Id">Profile or game id; <see cref="Guid.Empty"/> for the toggle hotkey.</param>
/// <param name="Name">Profile or game name; <c>null</c> for the toggle hotkey, whose label is the UI's business.</param>
public sealed record HotkeyUse(HotkeyUseKind Kind, Guid Id, string? Name);

/// <summary>
/// The one place that knows who holds a key combination inside RigShift. Windows registers a combination once, so a
/// profile, a game and the toggle hotkey all compete for the same keys – and the second one simply never fires.
/// </summary>
public static class HotkeyConflicts
{
    /// <summary>
    /// Who else uses <paramref name="hotkey"/>, or <c>null</c>. <paramref name="self"/> is the one being edited and never
    /// conflicts with itself. Profiles first, then games, then the toggle – the order they are registered in.
    /// </summary>
    public static HotkeyUse? Find(
        Hotkey hotkey, HotkeyUse self, IEnumerable<Profile> profiles, IEnumerable<GameEntry> games, Hotkey? toggle)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        ArgumentNullException.ThrowIfNull(self);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(games);

        foreach ((HotkeyUse use, Hotkey used) in All(profiles, games, toggle))
        {
            if (used == hotkey && !IsSame(use, self))
            {
                return use;
            }
        }

        return null;
    }

    /// <summary>Every valid hotkey with its owner, in registration order.</summary>
    public static IEnumerable<(HotkeyUse Use, Hotkey Hotkey)> All(IEnumerable<Profile> profiles, IEnumerable<GameEntry> games, Hotkey? toggle)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(games);

        foreach (Profile profile in profiles)
        {
            if (profile.Hotkey is { IsValid: true } hotkey)
            {
                yield return (new HotkeyUse(HotkeyUseKind.Profile, profile.Id, profile.Name), hotkey);
            }
        }

        foreach (GameEntry game in games)
        {
            if (game.Hotkey is { IsValid: true } hotkey)
            {
                yield return (new HotkeyUse(HotkeyUseKind.Game, game.Id, game.Name), hotkey);
            }
        }

        if (toggle is { IsValid: true })
        {
            yield return (new HotkeyUse(HotkeyUseKind.Toggle, Guid.Empty, null), toggle);
        }
    }

    private static bool IsSame(HotkeyUse a, HotkeyUse b) => a.Kind == b.Kind && a.Id == b.Id;
}
