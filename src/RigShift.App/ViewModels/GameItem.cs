using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Games;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>A game as shown on the games page and in the tray menu.</summary>
/// <param name="profileName">Name of the profile the game switches to, or <c>null</c> when it switches nothing.</param>
public sealed partial class GameItem(GameEntry game, string? profileName = null) : ObservableObject
{
    private IReadOnlyList<ImageSource>? _appIcons;

    public GameEntry Game { get; } = game;

    public string Name => Game.Name;

    public string? IconKey => ProfileIcons.Normalize(Game.Icon);

    public bool HasIcon => IconKey is not null;

    public string AccessibleName => IsRunning ? $"{Name}, {Loc.Instance["Game_Running"]}" : Name;

    /// <summary>"Steam · Rig" under the name: where the game comes from and what it switches to.</summary>
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

    /// <summary>"3 windows saved"; <c>null</c> when the entry has no layout.</summary>
    public string? WindowsLine => Game.WindowLayout is { IsEmpty: false } layout
        ? Loc.Format("Game_WindowsSaved", layout.Windows.Count)
        : null;

    public bool HasWindows => WindowsLine is not null;

    /// <summary>The companion programs' own icons, read when the card first shows them.</summary>
    public IReadOnlyList<ImageSource> AppIcons => _appIcons ??= Game.Apps
        .Select(app => Services.AppIcons.Load(app.Path))
        .OfType<ImageSource>()
        .ToList();

    /// <summary>A session for this game is running right now.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName), nameof(CanPlay))]
    public partial bool IsRunning { get; set; }

    /// <summary>Play is off while a session runs: starting twice would switch twice and start two sets of apps.</summary>
    public bool CanPlay => !IsRunning;

    /// <summary>Last result or "running"; <c>null</c> when nothing has happened yet.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; set; }

    private static string AppName(AppAction app) =>
        app.Name ?? (Path.GetFileNameWithoutExtension(app.Path) is { Length: > 0 } name ? name : app.Path);
}
