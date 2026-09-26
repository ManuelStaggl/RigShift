using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

/// <summary>
/// What a profile's plan means to the user – the same words in the profile list, the overview and the tray (v4 findings
/// U-10, U-11). A required display that is off is a warning, not a dead end: switching asks for it and waits. Only twin
/// displays that cannot be told apart block, because no waiting helps there.
/// </summary>
internal static class ProfileReadiness
{
    /// <returns>The status line and the next step for its tooltip; <c>null</c> as tip when nothing needs doing.</returns>
    public static (StatusKind Kind, string Text, string? Tip) Of(TopologyPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        // The display a Surround grid becomes is there once the switch built the grid (issue #9): nothing to report.
        MissingDisplay[] missing = [.. plan.Missing.Where(m => m.Reason != MissingReason.AwaitsSurround)];
        MissingDisplay[] required = [.. missing.Where(m => !m.Assignment.IsOptional)];
        MissingDisplay[] ambiguous = [.. required.Where(m => m.Reason == MissingReason.Ambiguous)];
        if (ambiguous.Length > 0)
        {
            return (StatusKind.Error, Loc.Instance["List_BlockedShort"], Loc.Format("Detail_AmbiguousNames", Names(ambiguous)));
        }

        if (required.Length > 0)
        {
            string text = required.Length == 1 ? Loc.Format("List_MissingOne", Names(required)) : Loc.Format("Head_MissingMany", required.Length);
            return (StatusKind.Warn, text, Loc.Format("Detail_BlockedNames", Names(required)));
        }

        return missing.Length > 0
            ? (StatusKind.Ok, Loc.Format("List_OptionalMissing", Names(missing)), null)
            : (StatusKind.Ok, Loc.Instance["List_Ready"], null);
    }

    public static string Names(IEnumerable<MissingDisplay> missing) =>
        string.Join(", ", missing.Select(m => SwitchMessages.NameOf(m.Assignment)));
}
