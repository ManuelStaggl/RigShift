using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.Core.Storage;

/// <summary>What kind of thing a backup would bring along.</summary>
public enum BackupItemKind
{
    /// <summary>A program a profile or a game starts.</summary>
    StartsProgram,

    /// <summary>A program a profile or a game ends – with its child processes.</summary>
    StopsProgram,

    /// <summary>A game's own executable.</summary>
    StartsGame,

    /// <summary>A USB rule that switches on its own.</summary>
    Rule,

    /// <summary>A system-wide key combination.</summary>
    Hotkey,
}

/// <summary>One line of the list shown before a backup is restored.</summary>
/// <param name="Owner">The profile or game it belongs to; for a rule, the profile it switches to.</param>
/// <param name="Detail">Path and arguments, or the rule's devices; empty for a key combination.</param>
/// <param name="IsNetworkPath">The program lies on a network share.</param>
/// <param name="IsNotFullPath">The program is named without a full path; RigShift will refuse to start it.</param>
/// <param name="SkipsConfirmation">The rule switches without the "keep this arrangement?" question.</param>
/// <param name="Keys">The key combination of a <see cref="BackupItemKind.Hotkey"/>; an empty owner means "previous profile".</param>
public sealed record BackupItem(
    BackupItemKind Kind,
    string Owner,
    string Detail,
    bool IsNetworkPath = false,
    bool IsNotFullPath = false,
    bool SkipsConfirmation = false,
    Hotkey? Keys = null)
{
    /// <summary>Worth a second look before saying yes.</summary>
    public bool NeedsAttention => IsNetworkPath || IsNotFullPath || SkipsConfirmation;
}

/// <summary>
/// A backup is more than layouts: it names programs that will be started and ended, rules that switch on their own
/// and keys that work everywhere. A ZIP from somebody else can therefore run whatever it likes on the next switch –
/// so before anything is restored, all of that is put in front of the user.
/// </summary>
public static class BackupReview
{
    public static IReadOnlyList<BackupItem> Review(BackupContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var items = new List<BackupItem>();
        var profileNames = content.Profiles.ToDictionary(p => p.Id, p => p.Name);

        foreach (Profile profile in content.Profiles)
        {
            AddApps(items, profile.Name, profile.Apps);
            if (profile.Hotkey is { } hotkey)
            {
                items.Add(new BackupItem(BackupItemKind.Hotkey, profile.Name, string.Empty, Keys: hotkey));
            }
        }

        foreach (GameEntry game in content.Games ?? [])
        {
            if (game.Launch.Kind == GameLaunchKind.Executable)
            {
                items.Add(Program(BackupItemKind.StartsGame, game.Name, game.Launch.Target, game.Launch.Arguments));
            }

            AddApps(items, game.Name, game.Apps);
            if (game.Hotkey is { } hotkey)
            {
                items.Add(new BackupItem(BackupItemKind.Hotkey, game.Name, string.Empty, Keys: hotkey));
            }
        }

        foreach (AutomationRule rule in content.Settings?.AutomationRules ?? [])
        {
            string devices = string.Join(", ", (rule.Devices ?? []).Select(d => d.Name ?? d.Id ?? "?"));
            if (devices.Length == 0)
            {
                devices = rule.LegacyUsbDeviceName ?? rule.LegacyUsbDeviceId ?? "?";
            }

            string owner = profileNames.TryGetValue(rule.ProfileId, out string? name) ? name : "?";
            items.Add(new BackupItem(BackupItemKind.Rule, owner, devices, SkipsConfirmation: rule.SkipConfirmation));
        }

        if (content.Settings?.ToggleHotkey is { } toggle)
        {
            items.Add(new BackupItem(BackupItemKind.Hotkey, string.Empty, string.Empty, Keys: toggle));
        }

        return items;
    }

    private static void AddApps(List<BackupItem> items, string owner, IReadOnlyList<AppAction> apps)
    {
        foreach (AppAction app in apps)
        {
            items.Add(app.Kind == AppActionKind.Start
                ? Program(BackupItemKind.StartsProgram, owner, app.Path, app.Arguments)
                : new BackupItem(BackupItemKind.StopsProgram, owner, LaunchPath.Expand(app.Path)));
        }
    }

    private static BackupItem Program(BackupItemKind kind, string owner, string path, string? arguments)
    {
        string file = LaunchPath.Expand(path);
        string detail = string.IsNullOrWhiteSpace(arguments) ? file : file + " " + arguments.Trim();
        return new BackupItem(kind, owner, detail, LaunchPath.IsNetwork(path), !LaunchPath.IsFullyQualified(path));
    }
}
