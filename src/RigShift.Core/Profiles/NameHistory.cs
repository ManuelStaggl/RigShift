namespace RigShift.Core.Profiles;

/// <summary>
/// Names a profile or game carried before a rename. The command line, <c>rigshift://</c> links, Stream Deck keys and
/// desktop shortcuts find things by name, and a rename must not break them.
/// </summary>
public static class NameHistory
{
    /// <summary>
    /// The item named <paramref name="name"/>, else one that was named so before, else the one with that id. A former
    /// name never wins over another item's current name. Matched case-insensitively and without surrounding blanks.
    /// </summary>
    public static T? Find<T>(IEnumerable<T> items, string name, Func<T, string> current, Func<T, IReadOnlyList<string>?> former, Func<T, Guid> id)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(name);
        List<T> all = [.. items];
        return all.FirstOrDefault(i => Same(current(i), name))
            ?? all.FirstOrDefault(i => former(i)?.Any(f => Same(f, name)) == true)
            ?? (Guid.TryParse(name.Trim(), out Guid guid) ? all.FirstOrDefault(i => id(i) == guid) : null);
    }

    /// <summary>The former names once the item stored as <paramref name="storedName"/> is saved as <paramref name="newName"/>.</summary>
    public static IReadOnlyList<string>? After(string storedName, IReadOnlyList<string>? former, string newName)
    {
        ArgumentNullException.ThrowIfNull(storedName);
        ArgumentNullException.ThrowIfNull(newName);
        List<string> names = [.. (former ?? []).Append(storedName)
            .Select(n => n.Trim())
            .Where(n => n.Length > 0 && !Same(n, newName))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
        return names.Count == 0 ? null : names;
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
