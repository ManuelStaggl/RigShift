namespace RigShift.Core.Games;

/// <summary>Helpers around a list of game entries. The counterpart of <c>ProfileEditing</c>, and far smaller.</summary>
public static class GameEditing
{
    /// <summary>Game names are matched case-insensitively and without surrounding blanks (CLI: <c>play iracing</c>).</summary>
    public static GameEntry? FindByName(IEnumerable<GameEntry> games, string name)
    {
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(name);
        return games.FirstOrDefault(g => string.Equals(g.Name.Trim(), name.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
