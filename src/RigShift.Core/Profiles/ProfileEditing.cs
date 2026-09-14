using RigShift.Core.Topology;

namespace RigShift.Core.Profiles;

/// <summary>
/// Rules for creating and editing profiles: capturing the live arrangement, choosing the primary display, names.
/// Positions are never typed in by the user – they come from the arrangement Windows reports (docs/PLAN.md, section 10).
/// </summary>
public static class ProfileEditing
{
    /// <summary>A new profile from the displays that are active right now, with the custom names in <paramref name="knownNames"/>.</summary>
    public static Profile Capture(
        string name, DisplaySnapshot snapshot, AudioAssignment? audio = null, IReadOnlyDictionary<string, string>? knownNames = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new Profile
        {
            Id = Guid.NewGuid(),
            Name = name.Trim(),
            Displays = CurrentArrangement(snapshot, [], knownNames),
            Audio = audio ?? new AudioAssignment(),
        };
    }

    /// <summary>
    /// The active displays with their current mode and position, left to right. Displays that were optional in
    /// <paramref name="previous"/> stay optional, unless they are the primary one. Custom names come from
    /// <paramref name="previous"/> or, for displays not in it, from <paramref name="knownNames"/> (<see cref="DisplayNames.Known"/>).
    /// </summary>
    public static IReadOnlyList<DisplayAssignment> CurrentArrangement(
        DisplaySnapshot snapshot, IEnumerable<DisplayAssignment> previous, IReadOnlyDictionary<string, string>? knownNames = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(previous);

        List<DisplayAssignment> before = previous.ToList();
        var names = new Dictionary<string, string>(knownNames ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        foreach (DisplayAssignment display in before)
        {
            if (DisplayNames.Normalize(display.CustomName) is { } name)
            {
                names[display.Identity.TargetDevicePath] = name;
            }
        }

        var optional = before
            .Where(d => d.IsOptional)
            .Select(d => d.Identity.TargetDevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return snapshot.Displays
            .Where(d => d.IsActive && d.ActiveMode is not null)
            .Select(d => d.ActiveMode! with
            {
                Identity = d.Identity,
                IsOptional = !d.ActiveMode.IsPrimary && optional.Contains(d.Identity.TargetDevicePath),
                CustomName = names.GetValueOrDefault(d.Identity.TargetDevicePath),
            })
            .OrderBy(d => d.PositionX)
            .ThenBy(d => d.PositionY)
            .ToList();
    }

    /// <summary>
    /// Makes the display at <paramref name="index"/> primary. Windows defines the primary display as the one at (0,0),
    /// so every position shifts by the same offset; the arrangement itself does not change. A primary display is
    /// never optional – without it there would be nothing to show the confirmation on.
    /// </summary>
    public static IReadOnlyList<DisplayAssignment> SetPrimary(IReadOnlyList<DisplayAssignment> displays, int index)
    {
        ArgumentNullException.ThrowIfNull(displays);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, displays.Count);

        int offsetX = displays[index].PositionX;
        int offsetY = displays[index].PositionY;
        return displays
            .Select((d, i) => d with
            {
                PositionX = d.PositionX - offsetX,
                PositionY = d.PositionY - offsetY,
                IsPrimary = i == index,
                IsOptional = i != index && d.IsOptional,
            })
            .ToList();
    }

    /// <summary>Profile names are matched case-insensitively and without surrounding blanks (CLI: <c>apply rig</c>).</summary>
    public static Profile? FindByName(IEnumerable<Profile> profiles, string name)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(name);
        return profiles.FirstOrDefault(p => NamesEqual(p.Name, name));
    }

    /// <summary><paramref name="baseName"/>, or <c>baseName 2</c>, <c>baseName 3</c>, … if taken.</summary>
    public static string UniqueName(string baseName, IEnumerable<string> existing)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        ArgumentNullException.ThrowIfNull(existing);

        var taken = existing.Select(n => n.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string trimmed = baseName.Trim();
        if (!taken.Contains(trimmed))
        {
            return trimmed;
        }

        for (int i = 2; ; i++)
        {
            string candidate = $"{trimmed} {i}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>Everything that prevents saving <paramref name="profile"/> next to <paramref name="others"/>.</summary>
    public static IReadOnlyList<ProfileProblem> Validate(Profile profile, IEnumerable<Profile> others)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(others);

        var problems = new List<ProfileProblem>();
        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            problems.Add(ProfileProblem.NameMissing);
        }
        else if (others.Any(o => o.Id != profile.Id && NamesEqual(o.Name, profile.Name)))
        {
            problems.Add(ProfileProblem.NameTaken);
        }

        if (profile.Hotkey is { } hotkey)
        {
            if (!hotkey.IsValid)
            {
                problems.Add(ProfileProblem.HotkeyInvalid);
            }
            else if (others.Any(o => o.Id != profile.Id && o.Hotkey == hotkey))
            {
                problems.Add(ProfileProblem.HotkeyTaken);
            }
        }

        if (profile.Apps.Any(a => string.IsNullOrWhiteSpace(a.Path)))
        {
            problems.Add(ProfileProblem.AppPathMissing);
        }

        if (profile.Displays.Count == 0)
        {
            problems.Add(ProfileProblem.NoDisplays);
            return problems;
        }

        List<DisplayAssignment> primaries = profile.Displays.Where(d => d.IsPrimary).ToList();
        if (primaries.Count != 1)
        {
            problems.Add(ProfileProblem.NoSinglePrimary);
        }
        else if (primaries[0].IsOptional)
        {
            problems.Add(ProfileProblem.PrimaryIsOptional);
        }

        return problems;
    }

    private static bool NamesEqual(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}

public enum ProfileProblem
{
    NameMissing,
    NameTaken,
    NoDisplays,
    NoSinglePrimary,
    PrimaryIsOptional,

    /// <summary>No modifier key, or only modifiers.</summary>
    HotkeyInvalid,

    /// <summary>Another profile uses the same key combination.</summary>
    HotkeyTaken,

    /// <summary>An app entry has no program.</summary>
    AppPathMissing,
}
