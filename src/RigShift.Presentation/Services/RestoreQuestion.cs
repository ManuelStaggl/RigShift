using RigShift.App.Localization;
using RigShift.Core.Storage;

namespace RigShift.App.Services;

/// <summary>One line of the restore question: what and whose, the path or keys, and why it deserves a second look.</summary>
public sealed record RestoreLine(string Heading, string Text, string? Warning = null);

/// <summary>
/// The words of the question before a backup is restored. A backup also brings programs to start and end, rules and
/// hotkeys – and it may come from somebody else – so all of those are listed, the ones that need attention first.
/// </summary>
public static class RestoreQuestion
{
    public static string Message(BackupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        IReadOnlyList<BackupItem> items = BackupReview.Review(content);
        string message = Loc.Format(content.Games is null ? "About_RestoreText" : "About_RestoreTextGames", content.Profiles.Count, content.Games?.Count ?? 0);
        if (items.Count > 0)
        {
            message += " " + Loc.Instance[items.Any(i => i.NeedsAttention) ? "About_RestoreReviewAttention" : "About_RestoreReview"];
        }

        return message;
    }

    public static IReadOnlyList<RestoreLine> Lines(BackupContent content) =>
        [.. BackupReview.Review(content).OrderByDescending(i => i.NeedsAttention).Select(Describe)];

    /// <summary>One backup item in words.</summary>
    public static RestoreLine Describe(BackupItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        string what = Loc.Instance[item.Kind switch
        {
            BackupItemKind.StartsProgram => "Restore_Starts",
            BackupItemKind.StopsProgram => "Restore_Stops",
            BackupItemKind.StartsGame => "Restore_Game",
            BackupItemKind.Rule => "Restore_Rule",
            _ => "Restore_Hotkey",
        }];
        string owner = item.Owner.Length > 0 ? item.Owner : Loc.Instance["Settings_ToggleHotkey"];
        string text = item.Keys is { } keys ? HotkeyFormat.Format(keys) : item.Detail;
        string? warning = item.IsNetworkPath ? Loc.Instance["Restore_WarnNetwork"]
            : item.IsNotFullPath ? Loc.Instance["Restore_WarnNotFullPath"]
            : item.SkipsConfirmation ? Loc.Instance["Restore_WarnNoConfirm"]
            : null;
        return new RestoreLine($"{what} · {owner}", text, warning);
    }
}
