using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.App.Services;

/// <summary>What a line of the tray menu does when clicked.</summary>
public enum TrayMenuCommand
{
    /// <summary>Nothing: a separator, or a line that only informs.</summary>
    None,
    SwitchProfile,
    PlayGame,
    SaveCurrent,
    Open,
    Settings,

    /// <summary>Pauses or resumes the USB rules; the line is a check box.</summary>
    PauseAutomation,
    InstallUpdate,
    Exit,
}

/// <summary>One line of the tray menu, without WPF: the tray icon turns it into a menu item and runs the command on a click.</summary>
public sealed record TrayMenuEntry(TrayMenuCommand Command, string Header)
{
    public static TrayMenuEntry Separator { get; } = new(TrayMenuCommand.None, string.Empty) { IsSeparator = true };

    public bool IsSeparator { get; private init; }

    /// <summary>The hotkey, shown on the right.</summary>
    public string Gesture { get; init; } = string.Empty;

    public bool IsEnabled { get; init; } = true;

    public bool IsCheckable { get; init; }

    public bool IsChecked { get; init; }

    /// <summary>The profile <see cref="TrayMenuCommand.SwitchProfile"/> switches to.</summary>
    public Profile? Profile { get; init; }

    /// <summary>The game <see cref="TrayMenuCommand.PlayGame"/> starts.</summary>
    public GameEntry? Game { get; init; }
}

/// <summary>What the tray menu is built from; read from the services each time the menu is rebuilt.</summary>
public sealed record TrayMenuState
{
    public IReadOnlyList<ProfileItem> Profiles { get; init; } = [];

    public IReadOnlyList<GameItem> Games { get; init; } = [];

    /// <summary>Games whose session runs; they cannot be started a second time.</summary>
    public IReadOnlySet<Guid> RunningGames { get; init; } = new HashSet<Guid>();

    /// <summary>There are USB rules; without any, pausing them means nothing.</summary>
    public bool HasAutomation { get; init; }

    public bool AutomationPaused { get; init; }

    /// <summary>The version that is downloaded or can be installed, or <c>null</c> when there is none.</summary>
    public string? UpdateVersion { get; init; }

    public bool CanInstallUpdate { get; init; }
}

/// <summary>
/// The tray's context menu as a plain list: the profiles, the games, the commands, then update and exit. It depends on the
/// state alone, so what the menu offers is testable without a tray icon.
/// </summary>
public static class TrayMenuModel
{
    public static IReadOnlyList<TrayMenuEntry> Build(TrayMenuState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var entries = new List<TrayMenuEntry>();
        foreach (ProfileItem item in state.Profiles)
        {
            entries.Add(new TrayMenuEntry(TrayMenuCommand.SwitchProfile, item.Name)
            {
                IsChecked = item.IsActive,
                Gesture = GestureOf(item.Profile.Hotkey),
                Profile = item.Profile,
            });
        }

        if (state.Profiles.Count == 0)
        {
            entries.Add(new TrayMenuEntry(TrayMenuCommand.None, Loc.Instance["Tray_NoProfiles"]) { IsEnabled = false });
        }

        // Games below the profiles, each starting its session. Flat rather than in a submenu: starting a race is the
        // one thing the tray is there for, and nobody has dozens of games configured.
        if (state.Games.Count > 0)
        {
            entries.Add(TrayMenuEntry.Separator);
            foreach (GameItem item in state.Games)
            {
                entries.Add(new TrayMenuEntry(TrayMenuCommand.PlayGame, Loc.Format("Tray_PlayGame", item.Name))
                {
                    IsEnabled = !state.RunningGames.Contains(item.Game.Id),
                    Gesture = GestureOf(item.Game.Hotkey),
                    Game = item.Game,
                });
            }
        }

        entries.Add(TrayMenuEntry.Separator);
        entries.Add(new TrayMenuEntry(TrayMenuCommand.SaveCurrent, Loc.Instance["Tray_SaveCurrent"]));
        entries.Add(new TrayMenuEntry(TrayMenuCommand.Open, Loc.Instance["Tray_Open"]));
        entries.Add(new TrayMenuEntry(TrayMenuCommand.Settings, Loc.Instance["Tray_Settings"]));
        if (state.HasAutomation)
        {
            entries.Add(new TrayMenuEntry(TrayMenuCommand.PauseAutomation, Loc.Instance["Automation_Pause"])
            {
                IsCheckable = true,
                IsChecked = state.AutomationPaused,
            });
        }

        entries.Add(TrayMenuEntry.Separator);
        if (state.UpdateVersion is { } version)
        {
            entries.Add(new TrayMenuEntry(TrayMenuCommand.InstallUpdate, Loc.Format("Tray_RestartToUpdate", version))
            {
                IsEnabled = state.CanInstallUpdate,
            });
        }

        entries.Add(new TrayMenuEntry(TrayMenuCommand.Exit, Loc.Instance["Tray_Exit"]));
        return entries;
    }

    private static string GestureOf(Hotkey? hotkey) => hotkey is null ? string.Empty : HotkeyFormat.Format(hotkey);
}
