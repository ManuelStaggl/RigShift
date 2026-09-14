namespace RigShift.Core.Profiles;

/// <summary>
/// Human-readable display names. A user-given name belongs to the monitor, not to one profile: it is stored on every
/// <see cref="DisplayAssignment"/> with the same target device path and kept in sync on save (docs/PLAN.md, section 6).
/// </summary>
public static class DisplayNames
{
    public const int MaxCustomNameLength = 40;

    public static string Of(DisplayIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return string.IsNullOrWhiteSpace(identity.FriendlyName) ? identity.TargetDevicePath : identity.FriendlyName;
    }

    /// <summary>"Left · CM27X3" for logs and messages; the model alone without a custom name.</summary>
    public static string Of(DisplayAssignment display)
    {
        ArgumentNullException.ThrowIfNull(display);
        return Label(display.CustomName, display.Identity, Of(display.Identity));
    }

    /// <summary>"Name · Model"; <paramref name="unnamed"/> stands in for a display without an EDID name.</summary>
    public static string Label(string? customName, DisplayIdentity identity, string unnamed)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string model = string.IsNullOrWhiteSpace(identity.FriendlyName) ? unnamed : identity.FriendlyName;
        return Normalize(customName) is { } custom ? $"{custom} · {model}" : model;
    }

    /// <summary>Trimmed and shortened to <see cref="MaxCustomNameLength"/>; <c>null</c> when empty.</summary>
    public static string? Normalize(string? customName)
    {
        string? trimmed = customName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxCustomNameLength)].TrimEnd();
    }

    /// <summary>
    /// Custom names by target device path: from the <paramref name="registry"/> of the settings first, then from the
    /// profiles.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Known(IEnumerable<Profile> profiles, IReadOnlyDictionary<string, string>? registry = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string value) in registry ?? new Dictionary<string, string>())
        {
            if (Normalize(value) is { } name)
            {
                names.TryAdd(path, name);
            }
        }

        foreach (DisplayAssignment display in profiles.SelectMany(p => p.Displays))
        {
            if (Normalize(display.CustomName) is { } name)
            {
                names.TryAdd(display.Identity.TargetDevicePath, name);
            }
        }

        return names;
    }

    /// <summary>The registry with <paramref name="name"/> set for <paramref name="targetDevicePath"/>, or removed when blank.</summary>
    public static IReadOnlyDictionary<string, string> WithName(
        IReadOnlyDictionary<string, string>? registry, string targetDevicePath, string? name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDevicePath);

        var names = new Dictionary<string, string>(registry ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        if (Normalize(name) is { } normalized)
        {
            names[targetDevicePath] = normalized;
        }
        else
        {
            names.Remove(targetDevicePath);
        }

        return names;
    }

    /// <summary>The profiles that contain the monitor under another name, with <paramref name="name"/> adopted.</summary>
    public static IReadOnlyList<Profile> Rename(IEnumerable<Profile> profiles, string targetDevicePath, string? name)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDevicePath);

        string? normalized = Normalize(name);
        return profiles
            .Where(p => p.Displays.Any(d => IsSame(d, targetDevicePath) && Normalize(d.CustomName) != normalized))
            .Select(p => p with
            {
                Displays = p.Displays.Select(d => IsSame(d, targetDevicePath) ? d with { CustomName = normalized } : d).ToList(),
            })
            .ToList();
    }

    /// <summary>
    /// The other profiles that show a display of <paramref name="saved"/> under a different name, with that name
    /// adopted – including a removed name. Only these need to be written.
    /// </summary>
    public static IReadOnlyList<Profile> Propagate(Profile saved, IEnumerable<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(saved);
        ArgumentNullException.ThrowIfNull(profiles);

        var names = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DisplayAssignment display in saved.Displays)
        {
            names[display.Identity.TargetDevicePath] = Normalize(display.CustomName);
        }

        var changed = new List<Profile>();
        foreach (Profile profile in profiles.Where(p => p.Id != saved.Id))
        {
            bool differs = false;
            List<DisplayAssignment> displays = profile.Displays
                .Select(d =>
                {
                    if (!names.TryGetValue(d.Identity.TargetDevicePath, out string? name) || Normalize(d.CustomName) == name)
                    {
                        return d;
                    }

                    differs = true;
                    return d with { CustomName = name };
                })
                .ToList();

            if (differs)
            {
                changed.Add(profile with { Displays = displays });
            }
        }

        return changed;
    }

    private static bool IsSame(DisplayAssignment display, string targetDevicePath) =>
        string.Equals(display.Identity.TargetDevicePath, targetDevicePath, StringComparison.OrdinalIgnoreCase);
}
