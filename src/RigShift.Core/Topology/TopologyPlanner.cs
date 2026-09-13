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
        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayIdentity wanted = assignments[i].Identity;
            AttachedDisplay? hit = snapshot.Displays.FirstOrDefault(d =>
                !claimed.Contains(d)
                && string.Equals(d.Identity.TargetDevicePath, wanted.TargetDevicePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(d.Identity.AdapterDevicePath, wanted.AdapterDevicePath, StringComparison.OrdinalIgnoreCase));
            if (hit is not null)
            {
                matches[i] = hit;
                claimed.Add(hit);
            }
        }

        // Pass 2: EDID fallback (port or cable changed). Only unambiguous candidates are accepted.
        for (int i = 0; i < assignments.Count; i++)
        {
            if (matches[i] is not null)
            {
                continue;
            }

            AttachedDisplay? hit = FindByEdid(assignments[i].Identity, snapshot, claimed);
            if (hit is not null)
            {
                matches[i] = hit;
                matchedByEdid[i] = true;
                claimed.Add(hit);
            }
        }

        var resolved = new List<PlannedDisplay>();
        var missing = new List<MissingDisplay>();
        var warnings = new List<PlanWarning>();

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
                            $"{DisplayNames.Of(assignment.Identity)} was matched by EDID at {match.Identity.TargetDevicePath} (port or cable changed).")));
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

        double refresh = assignment.RefreshDenominator == 0
            ? 0d
            : (double)assignment.RefreshNumerator / assignment.RefreshDenominator;
        double pixelRate = (double)assignment.Width * assignment.Height * refresh;
        return pixelRate > _options.DualHeadPixelRateThreshold ? 2 : 1;
    }

    private static AttachedDisplay? FindByEdid(DisplayIdentity wanted, DisplaySnapshot snapshot, HashSet<AttachedDisplay> claimed)
    {
        if (wanted.EdidManufacturerId == 0 && wanted.EdidProductCodeId == 0)
        {
            return null;
        }

        List<AttachedDisplay> candidates = snapshot.Displays
            .Where(d => !claimed.Contains(d)
                && d.Identity.EdidManufacturerId == wanted.EdidManufacturerId
                && d.Identity.EdidProductCodeId == wanted.EdidProductCodeId)
            .ToList();

        if (candidates.Count > 1)
        {
            candidates = candidates
                .Where(d => string.Equals(d.Identity.AdapterDevicePath, wanted.AdapterDevicePath, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return candidates.Count == 1 ? candidates[0] : null;
    }

    private List<PlanWarning> CheckHeadBudget(IEnumerable<PlannedDisplay> resolved)
    {
        // A warning, never a block: the heuristic must not prevent a switch that would actually work.
        var warnings = new List<PlanWarning>();
        foreach (IGrouping<string, PlannedDisplay> adapter in resolved.GroupBy(
            r => r.Target.Identity.AdapterDevicePath, StringComparer.OrdinalIgnoreCase))
        {
            int heads = adapter.Sum(r => EstimateHeads(r.Assignment));
            int budget = _options.HeadBudgetByAdapter.TryGetValue(adapter.Key, out int configured)
                ? configured
                : _options.DefaultHeadBudget;

            if (heads > budget)
            {
                string displays = string.Join(", ", adapter.Select(r => string.Create(CultureInfo.InvariantCulture,
                    $"{DisplayNames.Of(r.Assignment.Identity)} {r.Assignment.Width}x{r.Assignment.Height} ({EstimateHeads(r.Assignment)})")));
                warnings.Add(new PlanWarning(
                    PlanWarningKind.HeadBudgetExceeded,
                    string.Create(CultureInfo.InvariantCulture,
                        $"Adapter {adapter.Key} would drive an estimated {heads} display heads, budget is {budget}: {displays}. {HeadBudgetHint}")));
            }
        }

        return warnings;
    }
}
