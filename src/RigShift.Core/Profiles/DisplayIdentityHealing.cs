using RigShift.Core.Topology;

namespace RigShift.Core.Profiles;

/// <summary>
/// Keeps the saved identities of the monitors current (v4 finding K-03). After a switch that stayed, a monitor the planner
/// found anywhere but on its saved path – another port, a new graphics card – and one whose EDID serial number the profile
/// does not know yet are written back into every profile that contains it. The next switch then finds it by its path again,
/// and identical monitors stay apart even when all paths change at once.
/// </summary>
public static class DisplayIdentityHealing
{
    /// <summary>
    /// What <paramref name="plan"/> found out: the monitor saved under a path, and what it is now. A move to a path another
    /// display of <paramref name="profiles"/> already uses is left out: then the planner may have taken an identical monitor
    /// for one that is only switched off, and writing that down would make the guess permanent.
    /// </summary>
    public static IReadOnlyList<IdentityUpdate> Find(TopologyPlan plan, IEnumerable<Profile> profiles)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(profiles);

        var known = profiles.SelectMany(p => p.Displays).Select(d => d.Identity.TargetDevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updates = new List<IdentityUpdate>();
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            DisplayIdentity saved = planned.Assignment.Identity;
            DisplayIdentity now = Merge(saved, planned.Target.Identity);
            var update = new IdentityUpdate(saved.TargetDevicePath, now);
            if (Same(saved, now) || (update.Moved && known.Contains(now.TargetDevicePath)))
            {
                continue;
            }

            updates.Add(update);
        }

        return updates;
    }

    /// <summary>The profiles that contain an updated monitor, with its new identity; only these need to be written.</summary>
    public static IReadOnlyList<Profile> Apply(IEnumerable<Profile> profiles, IReadOnlyList<IdentityUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(updates);

        var byPath = new Dictionary<string, DisplayIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (IdentityUpdate update in updates)
        {
            byPath.TryAdd(update.SavedPath, update.Identity);
        }

        return profiles
            .Where(p => p.Displays.Any(d => byPath.ContainsKey(d.Identity.TargetDevicePath)))
            .Select(p => p with
            {
                Displays = [.. p.Displays.Select(d => byPath.TryGetValue(d.Identity.TargetDevicePath, out DisplayIdentity? identity) ? d with { Identity = identity } : d)],
            })
            .ToList();
    }

    /// <summary>
    /// A table keyed by target device path (custom names, curvature) with the entries of moved monitors under their new
    /// path; an entry the new path already has wins. The same instance when nothing moved, so saving can be skipped.
    /// </summary>
    public static IReadOnlyDictionary<string, T>? Rekey<T>(IReadOnlyDictionary<string, T>? table, IReadOnlyList<IdentityUpdate> updates)
    {
        ArgumentNullException.ThrowIfNull(updates);

        List<IdentityUpdate> moves = [.. updates.Where(u => u.Moved)];
        if (table is null || !table.Keys.Any(key => moves.Exists(m => string.Equals(m.SavedPath, key, StringComparison.OrdinalIgnoreCase))))
        {
            return table;
        }

        var rekeyed = new Dictionary<string, T>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, T value) in table)
        {
            rekeyed.TryAdd(key, value);
        }

        foreach (IdentityUpdate move in moves)
        {
            if (rekeyed.Remove(move.SavedPath, out T? value))
            {
                rekeyed.TryAdd(move.Identity.TargetDevicePath, value);
            }
        }

        return rekeyed;
    }

    /// <summary>The identity the display reports now; what it does not report (a failed EDID read) stays as saved.</summary>
    private static DisplayIdentity Merge(DisplayIdentity saved, DisplayIdentity live)
    {
        bool liveEdid = live.EdidManufacturerId != 0 || live.EdidProductCodeId != 0;
        return live with
        {
            EdidManufacturerId = liveEdid ? live.EdidManufacturerId : saved.EdidManufacturerId,
            EdidProductCodeId = liveEdid ? live.EdidProductCodeId : saved.EdidProductCodeId,
            EdidSerialHash = string.IsNullOrEmpty(live.EdidSerialHash) ? saved.EdidSerialHash : live.EdidSerialHash,
            FriendlyName = string.IsNullOrWhiteSpace(live.FriendlyName) ? saved.FriendlyName : live.FriendlyName,
        };
    }

    /// <summary>Paths compare like the planner compares them, without case; a different spelling is no reason to write.</summary>
    private static bool Same(DisplayIdentity a, DisplayIdentity b) =>
        string.Equals(a.TargetDevicePath, b.TargetDevicePath, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.AdapterDevicePath, b.AdapterDevicePath, StringComparison.OrdinalIgnoreCase)
        && a.EdidManufacturerId == b.EdidManufacturerId
        && a.EdidProductCodeId == b.EdidProductCodeId
        && string.Equals(a.EdidSerialHash, b.EdidSerialHash, StringComparison.Ordinal)
        && string.Equals(a.FriendlyName, b.FriendlyName, StringComparison.Ordinal);
}

/// <summary>The monitor saved under <paramref name="SavedPath"/> is <paramref name="Identity"/> now.</summary>
public sealed record IdentityUpdate(string SavedPath, DisplayIdentity Identity)
{
    /// <summary>The monitor is on another path now, not just better known.</summary>
    public bool Moved => !string.Equals(SavedPath, Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase);
}
