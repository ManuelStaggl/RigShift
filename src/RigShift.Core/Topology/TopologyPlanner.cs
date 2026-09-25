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
        var matchedBy = new MatchedBy[assignments.Count];
        var claimed = new HashSet<AttachedDisplay>(ReferenceEqualityComparer.Instance);

        void Match(int index, AttachedDisplay display, MatchedBy how)
        {
            matches[index] = display;
            matchedBy[index] = how;
            Claim(display, snapshot, claimed);
        }

        // Pass 1: device paths. Runs for all assignments first so that an EDID match can never steal
        // a display that another assignment identifies exactly (two identical desk monitors).
        // The same monitor can be listed on a stale and a live target; the available entry wins.
        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayIdentity wanted = assignments[i].Identity;
            AttachedDisplay? hit = snapshot.Displays
                .Where(d => !claimed.Contains(d)
                    && string.Equals(d.Identity.TargetDevicePath, wanted.TargetDevicePath, StringComparison.OrdinalIgnoreCase)
                    && SameAdapter(d.Identity, wanted))
                .OrderByDescending(d => d.IsAvailable)
                .FirstOrDefault();
            if (hit is not null)
            {
                Match(i, hit, MatchedBy.Path);
            }
        }

        var warnings = new List<PlanWarning>();

        // Pass 2: the serial number in the EDID. Exact like the path, so it too runs for every assignment before the passes
        // that go by the model: after a new graphics card or slot every path is new, and only the serial number still tells
        // identical monitors apart (v4 finding K-03). A serial number two displays of the profile share tells nothing.
        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayIdentity wanted = assignments[i].Identity;
            if (matches[i] is not null || !HasSerial(wanted)
                || Enumerable.Range(0, assignments.Count).Any(j => j != i && SameSerial(assignments[j].Identity, wanted)))
            {
                continue;
            }

            if (Only(PerTarget(snapshot.Displays.Where(d => !claimed.Contains(d) && SameSerial(d.Identity, wanted)))) is { } hit)
            {
                Match(i, hit, MatchedBy.Serial);
            }
        }

        // Pass 3: EDID model (port or cable changed). Only unambiguous candidates are accepted.
        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayIdentity wanted = assignments[i].Identity;
            if (matches[i] is not null || !HasEdid(wanted))
            {
                continue;
            }

            List<AttachedDisplay> candidates = PerTarget(snapshot.Displays.Where(d => !claimed.Contains(d) && SameEdid(d.Identity, wanted)));

            // The connector is only worth something when the graphics card itself got a new identity (another card, slot or
            // BIOS update): then every path changed, but the card numbers its ports as before.
            bool adapterGone = !snapshot.Displays.Any(d => SameAdapter(d.Identity, wanted));

            // Identical monitors in the profile without their ports: one candidate could be either of them (B-10). They are
            // only matched as a group, by their connectors, and only if every one of them is found that way.
            List<int> twins = [.. Enumerable.Range(0, assignments.Count).Where(j => matches[j] is null && SameEdid(assignments[j].Identity, wanted))];
            if (twins.Count > 1)
            {
                if (adapterGone && ByConnector(twins, assignments, candidates) is { } found)
                {
                    foreach ((int twin, AttachedDisplay display) in found)
                    {
                        Match(twin, display, MatchedBy.Connector);
                    }
                }
                else if (candidates.Count > 0)
                {
                    warnings.Add(new PlanWarning(
                        PlanWarningKind.AmbiguousTwin,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{DisplayNames.Of(assignments[i])} was not matched by EDID: {twins.Count} identical displays of the profile are not on their ports, {candidates.Count} candidate(s).")));
                }

                continue;
            }

            if (candidates.Count == 1)
            {
                Match(i, candidates[0], MatchedBy.Edid);
            }
            else if (Only(candidates.Where(d => SameAdapter(d.Identity, wanted))) is { } onAdapter)
            {
                Match(i, onAdapter, MatchedBy.Edid);
            }
            else if (adapterGone && Only(candidates.Where(d => SameConnector(d.Identity, wanted))) is { } onConnector)
            {
                Match(i, onConnector, MatchedBy.Connector);
            }
        }

        // Pass 4: the monitor's name. A display can answer its inputs with different hardware IDs – the Odyssey G93SC
        // reports one EDID over HDMI and another over DisplayPort – so after a cable swap neither the path nor the EDID
        // finds it again, and profiles written before this even stored an empty EDID. Only accepted when the name is
        // unique on both sides, so two identical monitors stay as ambiguous as they are for the EDID pass.
        for (int i = 0; i < assignments.Count; i++)
        {
            if (matches[i] is not null)
            {
                continue;
            }

            string wantedName = assignments[i].Identity.FriendlyName;
            if (string.IsNullOrWhiteSpace(wantedName))
            {
                continue;
            }

            // Monitors of the same model report the same name, so the name may only decide where the EDID cannot:
            // where it is unknown, or where no other unmatched display of the profile shares it. Otherwise this would
            // guess between two identical monitors, which is exactly what the EDID pass refuses to do.
            if (HasEdidTwin(assignments, matches, i))
            {
                continue;
            }

            int sameName = Enumerable.Range(0, assignments.Count)
                .Count(j => matches[j] is null && SameName(assignments[j].Identity.FriendlyName, wantedName));
            List<AttachedDisplay> candidates = NameCandidates(wantedName, snapshot, claimed);
            if (sameName > 1 || candidates.Count != 1)
            {
                if (sameName > 1 && candidates.Count > 0)
                {
                    warnings.Add(new PlanWarning(
                        PlanWarningKind.AmbiguousTwin,
                        string.Create(CultureInfo.InvariantCulture,
                            $"{DisplayNames.Of(assignments[i])} was not matched by name: {sameName} displays of the profile share it, {candidates.Count} candidate(s).")));
                }

                continue;
            }

            Match(i, candidates[0], MatchedBy.Name);
        }

        var resolved = new List<PlannedDisplay>();
        var missing = new List<MissingDisplay>();

        for (int i = 0; i < assignments.Count; i++)
        {
            DisplayAssignment assignment = assignments[i];
            AttachedDisplay? match = matches[i];

            if (match is null)
            {
                missing.Add(new MissingDisplay(
                    assignment, IsAmbiguous(assignments, matches, i, snapshot, claimed) ? MissingReason.Ambiguous : MissingReason.NotAttached));
            }
            else if (!match.IsAvailable)
            {
                missing.Add(new MissingDisplay(assignment, MissingReason.AttachedButUnavailable));
            }
            else
            {
                resolved.Add(new PlannedDisplay(assignment, match));
                if (FallbackWarning(assignment, match, matchedBy[i]) is { } warning)
                {
                    warnings.Add(warning);
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

    /// <summary>
    /// <c>\\?\DISPLAY#XEC2389#5&amp;101428be&amp;0&amp;UID4352#{…}</c> → <c>UID4352</c>: the port the graphics card reports the
    /// monitor on. The part before it is derived from the card's own identity and changes with it. <c>null</c> without one.
    /// </summary>
    private static string? Connector(string? targetDevicePath)
    {
        string[] parts = (targetDevicePath ?? string.Empty).Split('#');
        if (parts.Length < 3)
        {
            return null;
        }

        string last = parts[2][(parts[2].LastIndexOf('&') + 1)..];
        return last.Length > 3 && last.StartsWith("UID", StringComparison.OrdinalIgnoreCase) ? last : null;
    }

    /// <summary>
    /// Pairs every twin with the one candidate on its saved connector. <c>null</c> unless each finds exactly one and a
    /// different one – half a match would be a guess for the rest.
    /// </summary>
    private static List<(int Index, AttachedDisplay Display)>? ByConnector(
        List<int> twins, IReadOnlyList<DisplayAssignment> assignments, List<AttachedDisplay> candidates)
    {
        var found = new List<(int Index, AttachedDisplay Display)>();
        foreach (int twin in twins)
        {
            if (Only(candidates.Where(d => SameConnector(d.Identity, assignments[twin].Identity))) is not { } display
                || found.Exists(f => ReferenceEquals(f.Display, display)))
            {
                return null;
            }

            found.Add((twin, display));
        }

        return found;
    }

    /// <summary>
    /// Whether identical displays are attached and free – at least as many as the profile still misses of that model – so
    /// the display is most likely there and only could not be told apart from its twins (K-03). Identical means the same
    /// EDID model, or the same name where the EDID is unknown.
    /// </summary>
    private static bool IsAmbiguous(
        IReadOnlyList<DisplayAssignment> assignments, AttachedDisplay?[] matches, int index, DisplaySnapshot snapshot, HashSet<AttachedDisplay> claimed)
    {
        DisplayIdentity wanted = assignments[index].Identity;
        Func<DisplayIdentity, bool>? identical = HasEdid(wanted) ? d => SameEdid(d, wanted)
            : !string.IsNullOrWhiteSpace(wanted.FriendlyName) ? d => SameName(d.FriendlyName, wanted.FriendlyName)
            : null;
        if (identical is null)
        {
            return false;
        }

        int missing = Enumerable.Range(0, assignments.Count).Count(j => matches[j] is null && identical(assignments[j].Identity));
        int attached = PerTarget(snapshot.Displays.Where(d => !claimed.Contains(d) && identical(d.Identity))).Count;
        return attached > 0 && attached >= missing;
    }

    private static PlanWarning? FallbackWarning(DisplayAssignment assignment, AttachedDisplay match, MatchedBy how)
    {
        string? why = how switch
        {
            MatchedBy.Serial => "by its EDID serial number at {0} (port, cable or graphics card changed)",
            MatchedBy.Edid => "by EDID at {0} (port or cable changed)",
            MatchedBy.Connector => "by EDID and connector at {0} (graphics card or its slot changed)",
            MatchedBy.Name => "by name at {0} (input, port or cable changed)",
            _ => null,
        };

        return why is null
            ? null
            : new PlanWarning(
                how == MatchedBy.Name ? PlanWarningKind.MatchedByNameFallback : PlanWarningKind.MatchedByEdidFallback,
                $"{DisplayNames.Of(assignment)} was matched {string.Format(CultureInfo.InvariantCulture, why, match.Identity.TargetDevicePath)}.");
    }

    /// <summary>
    /// Displays that call themselves <paramref name="wantedName"/>, one entry per target and the available one first –
    /// the same rule the EDID pass uses, so a stale target never wins over a live one.
    /// </summary>
    private static List<AttachedDisplay> NameCandidates(string wantedName, DisplaySnapshot snapshot, HashSet<AttachedDisplay> claimed)
    {
        List<AttachedDisplay> candidates = PerTarget(snapshot.Displays.Where(d => !claimed.Contains(d) && SameName(d.Identity.FriendlyName, wantedName)));
        if (candidates.Count > 1)
        {
            candidates = [.. candidates.Where(d => d.IsAvailable)];
        }

        return candidates;
    }

    /// <summary>One entry per target, the available one first: a stale target never wins over a live one.</summary>
    private static List<AttachedDisplay> PerTarget(IEnumerable<AttachedDisplay> displays) =>
    [
        .. displays
            .GroupBy(d => d.Identity.TargetDevicePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(d => d.IsAvailable).First()),
    ];

    private static AttachedDisplay? Only(IEnumerable<AttachedDisplay> displays) =>
        displays.Take(2).ToList() is [var only] ? only : null;

    /// <summary>
    /// Whether another display of the profile that is still unmatched carries the same known EDID – then the two are
    /// identical monitors and nothing but their port tells them apart. An unknown EDID (0/0) is not an identity and
    /// never makes a twin.
    /// </summary>
    private static bool HasEdidTwin(IReadOnlyList<DisplayAssignment> assignments, AttachedDisplay?[] matches, int index)
    {
        DisplayIdentity wanted = assignments[index].Identity;
        if (!HasEdid(wanted))
        {
            return false;
        }

        return Enumerable.Range(0, assignments.Count)
            .Any(j => j != index && matches[j] is null && SameEdid(assignments[j].Identity, wanted));
    }

    private static bool SameName(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool HasEdid(DisplayIdentity identity) => identity.EdidManufacturerId != 0 || identity.EdidProductCodeId != 0;

    private static bool SameEdid(DisplayIdentity a, DisplayIdentity b) =>
        HasEdid(a)
        && a.EdidManufacturerId == b.EdidManufacturerId
        && a.EdidProductCodeId == b.EdidProductCodeId;

    private static bool HasSerial(DisplayIdentity identity) => !string.IsNullOrEmpty(identity.EdidSerialHash);

    /// <summary>Same model and same serial number: the same monitor, whatever port it is on.</summary>
    private static bool SameSerial(DisplayIdentity a, DisplayIdentity b) =>
        HasSerial(a) && SameEdid(a, b) && string.Equals(a.EdidSerialHash, b.EdidSerialHash, StringComparison.Ordinal);

    private static bool SameAdapter(DisplayIdentity a, DisplayIdentity b) =>
        string.Equals(a.AdapterDevicePath, b.AdapterDevicePath, StringComparison.OrdinalIgnoreCase);

    private static bool SameConnector(DisplayIdentity a, DisplayIdentity b) =>
        Connector(a.TargetDevicePath) is { } connector && string.Equals(connector, Connector(b.TargetDevicePath), StringComparison.OrdinalIgnoreCase);

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

    private enum MatchedBy
    {
        None,
        Path,
        Serial,
        Edid,
        Connector,
        Name,
    }
}
