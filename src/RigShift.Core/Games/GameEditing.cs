using RigShift.Core.Profiles;

namespace RigShift.Core.Games;

/// <summary>Rules for editing game entries. The counterpart of <c>ProfileEditing</c>, and far smaller.</summary>
public static class GameEditing
{
    /// <summary>Game names are matched case-insensitively and without surrounding blanks (CLI: <c>play iracing</c>).</summary>
    public static GameEntry? FindByName(IEnumerable<GameEntry> games, string name)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(name);
        return games.FirstOrDefault(g => NamesEqual(g.Name, name));
    }

    /// <summary>
    /// Everything that prevents saving <paramref name="game"/> next to <paramref name="others"/>. The hotkey is checked
    /// against the profiles as well: Windows registers a combination once, so the second owner would never fire.
    /// </summary>
    public static IReadOnlyList<GameProblem> Validate(GameEntry game, IEnumerable<GameEntry> others, IEnumerable<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(game);
        ArgumentNullException.ThrowIfNull(others);
        ArgumentNullException.ThrowIfNull(profiles);

        var problems = new List<GameProblem>();
        if (string.IsNullOrWhiteSpace(game.Name))
        {
            problems.Add(GameProblem.NameMissing);
        }
        else if (game.Name.Trim().Length > GameEntry.MaxNameLength)
        {
            problems.Add(GameProblem.NameTooLong);
        }
        else if (others.Any(o => o.Id != game.Id && NamesEqual(o.Name, game.Name)))
        {
            problems.Add(GameProblem.NameTaken);
        }

        if (string.IsNullOrWhiteSpace(game.Launch.Target))
        {
            problems.Add(GameProblem.LaunchMissing);
        }

        if (game.Hotkey is { } hotkey)
        {
            if (!hotkey.IsValid)
            {
                problems.Add(GameProblem.HotkeyInvalid);
            }
            else if (others.Any(o => o.Id != game.Id && o.Hotkey == hotkey) || profiles.Any(p => p.Hotkey == hotkey))
            {
                problems.Add(GameProblem.HotkeyTaken);
            }
        }

        if (game.Apps.Any(a => string.IsNullOrWhiteSpace(a.Path)))
        {
            problems.Add(GameProblem.AppPathMissing);
        }

        return problems;
    }

    private static bool NamesEqual(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum GameProblem
{
    NameMissing,

    /// <summary>Longer than <see cref="GameEntry.MaxNameLength"/>.</summary>
    NameTooLong,
    NameTaken,

    /// <summary>Nothing chosen to start.</summary>
    LaunchMissing,

    /// <summary>No modifier key, or only modifiers.</summary>
    HotkeyInvalid,

    /// <summary>Another game or a profile uses the same key combination.</summary>
    HotkeyTaken,

    /// <summary>A tool entry has no program.</summary>
    AppPathMissing,
}
