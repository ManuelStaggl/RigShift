using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The configured games. Lives on the UI thread; the store does its file work on the thread pool. Simpler than
/// <see cref="ProfileCatalog"/> on purpose – a game has no "active" state to work out, it either runs or it does not.
/// </summary>
public sealed partial class GameCatalog : ObservableObject
{
    private readonly IGameStore _store;
    private readonly ProfileCatalog _profiles;
    private readonly ILogger _log;
    private IReadOnlyList<GameEntry> _games = [];

    public GameCatalog(IGameStore store, ProfileCatalog profiles, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(log);

        _store = store;
        _profiles = profiles;
        _log = log.ForContext<GameCatalog>();
        IsEmpty = true;

        // The cards name the profile a game switches to, so a renamed profile has to reach them. Only the profiles
        // themselves: a display change is no reason to rebuild every game (v4 finding A-03).
        _profiles.ProfilesChanged += (_, _) => Rebuild();
        Loc.Instance.PropertyChanged += (_, _) => Rebuild();
    }

    /// <summary>Raised after the games changed.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<GameItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasGames))]
    public partial bool IsEmpty { get; set; }

    /// <summary>The page header only offers the scan while there is a list; empty, the big button does it.</summary>
    public bool HasGames => !IsEmpty;

    [ObservableProperty]
    public partial string? EmptyMessage { get; set; }

    /// <summary><c>true</c> when the games file could not be read – an empty list would be a lie then.</summary>
    [ObservableProperty]
    public partial bool IsUnreadable { get; set; }

    [ObservableProperty]
    public partial string? UnreadableMessage { get; set; }

    public IReadOnlyList<GameEntry> Games => _games;

    public GameEntry? Find(Guid id) => _games.FirstOrDefault(g => g.Id == id);

    /// <summary>The name of the profile a game switches to, for the detail head; <c>null</c> when it is gone.</summary>
    public string? ProfileNameOf(Guid profileId) => _profiles.Find(profileId)?.Name;

    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            GameLoadResult result = await _store.LoadAllAsync(cancellationToken);
            _games = result.Games;
            IsUnreadable = !result.IsComplete;
            UnreadableMessage = result.Unreadable;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Games could not be loaded");
            _games = [];
            IsUnreadable = true;
            UnreadableMessage = ex.Message;
        }

        Rebuild();
    }

    public async Task SaveAsync(GameEntry game, CancellationToken cancellationToken)
    {
        await _store.SaveAsync(game, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    public async Task DeleteAsync(Guid gameId, CancellationToken cancellationToken)
    {
        await _store.DeleteAsync(gameId, cancellationToken);
        await ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Writes back the process name a first start learned. Quiet: it happens while the user is in the game, and a
    /// failure here only means the next start learns it again.
    /// </summary>
    public async Task RememberProcessNameAsync(Guid gameId, string processName, CancellationToken cancellationToken)
    {
        if (Find(gameId) is not { } game || game.Launch.ProcessName == processName)
        {
            return;
        }

        try
        {
            await _store.SaveAsync(game with { Launch = game.Launch with { ProcessName = processName } }, cancellationToken);
            await ReloadAsync(cancellationToken);
            _log.Information("Game {Game}: process name {Process} remembered", game.Name, processName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Warning(ex, "Game {Game}: the learned process name could not be saved", game.Name);
        }
    }

    private void Rebuild()
    {
        Items.Clear();
        foreach (GameEntry game in _games)
        {
            Items.Add(new GameItem(game, _profiles.Find(game.ProfileId ?? Guid.Empty)?.Name));
        }

        IsEmpty = Items.Count == 0;
        EmptyMessage = Loc.Instance["Games_EmptyText"];
        Changed?.Invoke(this, EventArgs.Empty);
        _ = LoadIconsAsync([.. Items]);
    }

    /// <summary>
    /// The games' own icons, after the cards are up: finding a store game's executable can mean walking its install
    /// folder, and no list is worth blocking for an icon. Each item keeps its symbol until its icon arrives.
    /// </summary>
    private static async Task LoadIconsAsync(IReadOnlyList<GameItem> items)
    {
        foreach (GameItem item in items)
        {
            item.GameIcon = await GameIcons.LoadAsync(item.Game.Launch, Log.Logger);
        }
    }
}
