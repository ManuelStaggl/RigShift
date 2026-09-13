using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// Recognises which profile the live topology corresponds to – also when the user switched in the Windows settings.
/// A profile is active when its required displays are active at the stored positions with the stored primary,
/// active optional displays sit at their positions, and no other display is active. Resolution and refresh rate
/// are ignored on purpose: changing them in Windows does not make the desk a different place.
/// </summary>
public sealed class ActiveProfileMatcher
{
    private readonly TopologyPlanner _planner;

    public ActiveProfileMatcher(TopologyPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    public Profile? FindActive(IEnumerable<Profile> profiles, DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(snapshot);

        Profile? best = null;
        int bestScore = 0;
        foreach (Profile profile in profiles)
        {
            int score = Score(profile, snapshot);
            if (score > bestScore)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>Number of matching active displays, or 0 if the profile is not active.</summary>
    private int Score(Profile profile, DisplaySnapshot snapshot)
    {
        TopologyPlan plan = _planner.Plan(profile, snapshot);
        if (plan.IsBlocked)
        {
            return 0;
        }

        var claimed = new HashSet<AttachedDisplay>(ReferenceEqualityComparer.Instance);
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            AttachedDisplay target = planned.Target;
            if (!target.IsActive || target.ActiveMode is not { } mode)
            {
                if (!planned.Assignment.IsOptional)
                {
                    return 0;
                }

                continue;
            }

            if (mode.PositionX != planned.Assignment.PositionX
                || mode.PositionY != planned.Assignment.PositionY
                || mode.IsPrimary != planned.Assignment.IsPrimary)
            {
                return 0;
            }

            claimed.Add(target);
        }

        bool strangerActive = snapshot.Displays.Any(d => d.IsActive && !claimed.Contains(d));
        return strangerActive ? 0 : claimed.Count;
    }
}
