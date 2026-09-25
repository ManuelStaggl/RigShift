using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// Recognises which profile the live topology corresponds to – also when the user switched in the Windows settings.
/// A profile is active when its required displays are active at the stored positions with the stored primary,
/// active optional displays sit at their positions, and no other display is active. Resolution and refresh rate
/// are ignored on purpose: changing them in Windows does not make the desk a different place. They only decide between
/// profiles with the same layout.
/// </summary>
public sealed class ActiveProfileMatcher
{
    private readonly TopologyPlanner _planner;

    public ActiveProfileMatcher(TopologyPlanner planner)
    {
        ArgumentNullException.ThrowIfNull(planner);
        _planner = planner;
    }

    /// <summary>What switching to <paramref name="profile"/> would find: the displays it resolves and those it misses.</summary>
    public TopologyPlan Plan(Profile profile, DisplaySnapshot snapshot) => _planner.Plan(profile, snapshot);

    /// <param name="lastApplied">
    /// The profile RigShift applied last. Among profiles that fit the displays equally well – the same layout saved twice,
    /// once with HDR "don't change" – it wins over the one first in the list (v4 finding K-13).
    /// </param>
    public Profile? FindActive(IEnumerable<Profile> profiles, DisplaySnapshot snapshot, Guid? lastApplied = null)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(snapshot);

        Profile? best = null;
        (int Displays, int Modes, bool LastApplied) bestScore = default;
        foreach (Profile profile in profiles)
        {
            (int displays, int modes) = Score(profile, snapshot);
            (int, int, bool) score = (displays, modes, profile.Id == lastApplied);
            if (displays > 0 && score.CompareTo(bestScore) > 0)
            {
                best = profile;
                bestScore = score;
            }
        }

        return best;
    }

    /// <summary>
    /// Number of matching active displays, or 0 if the profile is not active; and how many of them also run the profile's
    /// resolution, refresh rate and HDR state. That second number tells "Rig 144 Hz" from "Rig 240 Hz", which have the same
    /// layout – before, the one first in the list won, and the USB automation then skipped its end (K-13).
    /// </summary>
    private (int Displays, int Modes) Score(Profile profile, DisplaySnapshot snapshot)
    {
        TopologyPlan plan = _planner.Plan(profile, snapshot);
        if (plan.IsBlocked)
        {
            return (0, 0);
        }

        var claimed = new HashSet<AttachedDisplay>(ReferenceEqualityComparer.Instance);
        int modes = 0;
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            AttachedDisplay target = planned.Target;
            DisplayAssignment wanted = planned.Assignment;
            if (!target.IsActive || target.ActiveMode is not { } mode)
            {
                if (!wanted.IsOptional)
                {
                    return (0, 0);
                }

                continue;
            }

            if (mode.PositionX != wanted.PositionX || mode.PositionY != wanted.PositionY || mode.IsPrimary != wanted.IsPrimary)
            {
                return (0, 0);
            }

            claimed.Add(target);
            if (mode.Width == wanted.Width && mode.Height == wanted.Height
                && Math.Abs(RefreshRate.Of(mode).Hertz - RefreshRate.Of(wanted).Hertz) < 0.5
                && (wanted.Hdr is null || mode.Hdr == wanted.Hdr))
            {
                modes++;
            }
        }

        bool strangerActive = snapshot.Displays.Any(d => d.IsActive && !claimed.Contains(d));
        return strangerActive ? (0, 0) : (claimed.Count, modes);
    }
}
