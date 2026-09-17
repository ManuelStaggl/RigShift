using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>A game as shown in the games list, the tray popup and the tray menu.</summary>
/// <param name="profileName">Name of the profile the game switches to, or <c>null</c> when it switches nothing.</param>
public sealed partial class GameItem(GameEntry game, string? profileName = null) : ObservableObject
{
    private IReadOnlyList<ImageSource>? _appIcons;

    public GameEntry Game { get; } = game;

    public string Name => Game.Name;

    public string? IconKey => ProfileIcons.Normalize(Game.Icon);

    public bool HasIcon => IconKey is not null;

    /// <summary>
    /// The game's own icon, filled in after the list is up (<see cref="Services.GameIcons"/>). A symbol the user
    /// picked wins over it: that choice was deliberate.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGameIcon), nameof(HasFallbackIcon))]
    public partial ImageSource? GameIcon { get; set; }

    public bool HasGameIcon => IconKey is null && GameIcon is not null;

    /// <summary>Neither a symbol nor an icon: the row still needs something, or it looks unfinished.</summary>
    public bool HasFallbackIcon => IconKey is null && GameIcon is null;

    public string AccessibleName => IsRunning ? $"{Name}, {Loc.Instance["Game_Running"]}" : Name;

    /// <summary>"Play iRacing": the tray row is an action, not a name.</summary>
    public string PlayText => Loc.Format("Tray_PlayGame", Name);

    /// <summary>"Ctrl+Alt+F5" for the tray row; empty without a hotkey.</summary>
    public string HotkeyText => Game.Hotkey is { } hotkey ? Services.HotkeyFormat.Format(hotkey) : string.Empty;

    /// <summary>The tray row shows either "running" or the hotkey, never both.</summary>
    public bool ShowsHotkey => !IsRunning && HotkeyText.Length > 0;

    /// <summary>"Steam · Rig" under the name in the tray popup: where the game comes from and what it switches to.</summary>
    public string SourceText
    {
        get
        {
            string source = Game.Launch.Kind switch
            {
                GameLaunchKind.Steam => "Steam",
                GameLaunchKind.Epic => "Epic",
                _ => Path.GetFileName(Game.Launch.Target),
            };

            return profileName is null ? source : $"{source} · {profileName}";
        }
    }

    /// <summary>"SimHub, Crew Chief"; <c>null</c> without companion apps.</summary>
    public string? AppsLine { get; } = game.Apps.Count == 0
        ? null
        : string.Join(", ", game.Apps.Select(AppName));

    public bool HasApps => AppsLine is not null;

    /// <summary>"3 window positions"; <c>null</c> when the entry has no layout.</summary>
    public string? WindowsLine => Game.WindowLayout is { IsEmpty: false } layout
        ? layout.Windows.Count == 1
            ? Loc.Instance["Game_WindowsSavedOne"]
            : Loc.Format("Game_WindowsSaved", layout.Windows.Count)
        : null;

    public bool HasWindows => WindowsLine is not null;

    /// <summary>The companion programs' own icons, read when the popup first shows them.</summary>
    public IReadOnlyList<ImageSource> AppIcons => _appIcons ??= Game.Apps
        .Select(app => Services.AppIcons.Load(app.Path))
        .OfType<ImageSource>()
        .ToList();

    /// <summary>A session for this game is running right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(CanPlay), nameof(ShowsHotkey))]
    public partial bool IsRunning { get; set; }

    /// <summary>Play is off while a session runs: starting twice would switch twice and start two sets of apps (R-FLOW-4).</summary>
    public bool CanPlay => !IsRunning && !IsNew;

    /// <summary>Last result or "running"; <c>null</c> when nothing has happened yet.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    /// <summary>A game that exists only in the detail so far ("New game" at the top of the list).</summary>
    public bool IsNew { get; init; }

    /// <summary>The status line under the name in the list: running, ready with the last session, learning, failed.</summary>
    [ObservableProperty]
    public partial StatusKind ListKind { get; private set; } = StatusKind.Neutral;

    [ObservableProperty]
    public partial string ListStatus { get; private set; } = string.Empty;

    public void SetStatus(StatusKind kind, string text)
    {
        ListKind = kind;
        ListStatus = text;
    }

    private static string AppName(AppAction app) =>
        app.Name ?? (Path.GetFileNameWithoutExtension(app.Path) is { Length: > 0 } name ? name : app.Path);
}
