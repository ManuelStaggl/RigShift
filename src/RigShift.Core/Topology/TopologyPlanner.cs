using System.Globalization;
using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// Matches a <see cref="Profile"/> against a <see cref="DisplaySnapshot"/>. Pure: no OS calls, no clock, no logging.
/// Rules: docs/display-topology.md.
/// </summary>
public sealed class TopologyPlanner
{
    private const string HeadBudgetHint =
        "Displays with a high pixel rate need Display Stream Compression and count as two heads. " +
        "The switch is attempted anyway; adjust the budget if your GPU allows more.";

    private readonly TopologyPlannerOptions _options;

    public TopologyPlanner(TopologyPlannerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    public TopologyPlan Plan(Profile profile, DisplaySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(snapshot);

        IReadOnlyList<DisplayAssignment> assignments = profile.Displays;
        var matches = new AttachedDisplay?[assignments.Count];
        var matchedByEdid = new bool[assignments.Count];
        var claimed = new HashSet<AttachedDisplay>(ReferenceEqualityComparer.Instance);

        // Pass 1: device paths. Runs for all assignments first so that an EDID match can never steal
        // a display that another assignment identifies exactly (two identical desk monitors).
        // The same monitor can be listed on a stale and a live target; the available entry wins.
        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayIdentity wanted = assignments[i].Identity;
            AttachedDisplay? hit = snapshot.Displays
                .Where(d => !claimed.Contains(d)
                    && string.Equals(d.Identity.TargetDevicePath, wanted.TargetDevicePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(d.Identity.AdapterDevicePath, wanted.AdapterDevicePath, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(d => d.IsAvailable)
                .FirstOrDefault();
            if (hit is not null)
            {
                matches[i] = hit;
                Claim(hit, snapshot, claimed);
            }
        }

        var warnings = new List<PlanWarning>();

        // Pass 2: EDID fallback (port or cable changed). Only unambiguous candidates are accepted.
        for (int i = 0; i < assignments.Count; i++)
        {
            if (matches[i] is not null)
            {
                continue;
            }

            DisplayIdentity wanted = assignments[i].Identity;
            List<AttachedDisplay> candidates = EdidCandidates(wanted, snapshot, claimed);

            // Identical monitors in the profile without their ports: one candidate could be either of them (B-10).
            int unmatchedTwins = Enumerable.Range(0, assignments.Count).Count(j => matches[j] is null && SameEdid(assignments[j].Identity, wanted));
            if (unmatchedTwins > 1)
            {
                if (candidates.Count > 0)
                {
                    warnings.Add(new PlanWarning(
                        PlanWarningKind.AmbiguousTwin,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{DisplayNames.Of(assignments[i])} was not matched by EDID: {unmatchedTwins} identical displays of the profile are not on their ports, {candidates.Count} candidate(s).")));
                }

                continue;
            }

            AttachedDisplay? hit = candidates.Count == 1 ? candidates[0] : null;
            if (hit is not null)
            {
                matches[i] = hit;
                matchedByEdid[i] = true;
                Claim(hit, snapshot, claimed);
            }
        }

        var resolved = new List<PlannedDisplay>();
        var missing = new List<MissingDisplay>();

        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayAssignment assignment = assignments[i];
            AttachedDisplay? match = matches[i];

            if (match is null)
            {
                missing.Add(new MissingDisplay(assignment, MissingReason.NotAttached));
            }
            else if (!match.IsAvailable)
            {
                missing.Add(new MissingDisplay(assignment, MissingReason.AttachedButUnavailable));
            }
            else
            {
                resolved.Add(new PlannedDisplay(assignment, match));
                if (matchedByEdid[i])
                {
                    warnings.Add(new PlanWarning(
                        PlanWarningKind.MatchedByEdidFallback,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{DisplayNames.Of(assignment)} was matched by EDID at {match.Identity.TargetDevicePath} (port or cable changed).")));
                }
            }
        }

        if (resolved.Count > 0 && !resolved.Any(r => r.Assignment.IsPrimary))
        {
            warnings.Add(new PlanWarning(PlanWarningKind.NoPrimary, "No display in the plan is primary; Windows will choose one."));
        }

        warnings.AddRange(CheckHeadBudget(resolved));

        return new TopologyPlan
        {
            Profile = profile,
            Resolved = resolved,
            Missing = missing,
            Warnings = warnings,
        };
    }

    /// <summary>Estimated display heads a mode occupies: two above the DSC pixel-rate threshold, otherwise one.</summary>
    public int EstimateHeads(DisplayAssignment assignment)
    {
        ArgumentNullException.ThrowIfNull(assignment);

        double pixelRate = (double)assignment.Width * assignment.Height * RefreshRate.Of(assignment).Hertz;
        return pixelRate > _options.DualHeadPixelRateThreshold ? 2 : 1;
    }

    /// <summary>Unclaimed displays with the wanted EDID, one per target path (the available entry of a duplicate).</summary>
    private static List<AttachedDisplay> EdidCandidates(DisplayIdentity wanted, DisplaySnapshot snapshot, HashSet<AttachedDisplay> claimed)
    {
        if (wanted.EdidManufacturerId == 0 && wanted.EdidProductCodeId == 0)
        {
            return [];
        }

        List<AttachedDisplay> candidates = snapshot.Displays
            .Where(d => !claimed.Contains(d) && SameEdid(d.Identity, wanted))
            .GroupBy(d => d.Identity.TargetDevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.IsAvailable).First())
            .ToList();

        if (candidates.Count > 1)
        {
            candidates = candidates
                .Where(d => string.Equals(d.Identity.AdapterDevicePath, wanted.AdapterDevicePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return candidates;
    }

    private static bool SameEdid(DisplayIdentity a, DisplayIdentity b) =>
        (a.EdidManufacturerId != 0 || a.EdidProductCodeId != 0)
        && a.EdidManufacturerId == b.EdidManufacturerId
        && a.EdidProductCodeId == b.EdidProductCodeId;

    /// <summary>Claims the display and every other entry of the same target, so no other assignment gets a duplicate of it.</summary>
    private static void Claim(AttachedDisplay display, DisplaySnapshot snapshot, HashSet<AttachedDisplay> claimed)
    {
        foreach (AttachedDisplay entry in snapshot.Displays)
        {
            if (string.Equals(entry.Identity.TargetDevicePath, display.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase))
            {
                claimed.Add(entry);
            }
        }
    }

    private List<PlanWarning> CheckHeadBudget(IEnumerable<PlannedDisplay> resolved)
    {
        // A warning, never a block: the heuristic must not prevent a switch that would actually work.
        var warnings = new List<PlanWarning>();
        foreach (IGrouping<string, PlannedDisplay> adapter in resolved.GroupBy(
            r => r.Target.Identity.AdapterDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            if (_options.HeadBudgetFor(adapter.Key) is not { } budget)
            {
                continue;
            }

            int heads = adapter.Sum(r => EstimateHeads(r.Assignment));
            if (heads > budget)
            {
                string displays = string.Join(", ", adapter.Select(r => string.Create(CultureInfo.InvariantCulture,
                    $"{DisplayNames.Of(r.Assignment)} {r.Assignment.Width}x{r.Assignment.Height} ({EstimateHeads(r.Assignment)})")));
                warnings.Add(new PlanWarning(
                    PlanWarningKind.HeadBudgetExceeded,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Adapter {adapter.Key} would drive an estimated {heads} display heads, budget is {budget}: {displays}. {HeadBudgetHint}")));
            }
        }

        return warnings;
    }
}
