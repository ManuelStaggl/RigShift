using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The games page: the master list on the left, the selected game's detail (its editor, tabs in session order) on the
/// right (R-NAV-2, F5). A running session marks its game in the list and locks "Play" (R-FLOW-4).
/// </summary>
public sealed partial class GamesViewModel : MasterDetailViewModel<GameItem, GameEditorViewModel>
{
    /// <summary>The tab that is the first decision a new game needs (F5).</summary>
    private const int ProfileTab = 1;

    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;
    private readonly IGamePageDialogs _dialogs;
    private readonly AppPaths _paths;
    private readonly Dictionary<Guid, GameSessionEvent> _lastEnded = [];
    private bool _openProfileTabAfterLoad;

    public GamesViewModel(GameCatalog catalog, GameSessionService sessions, IGamePageDialogs dialogs, AppPaths paths, ILogger log)
        : base(log?.ForContext<GamesViewModel>() ?? throw new ArgumentNullException(nameof(log)))
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(paths);

        _catalog = catalog;
        _sessions = sessions;
        _dialogs = dialogs;
        _paths = paths;

        catalog.Changed += (_, _) => OnUi(Rebuild);
        sessions.SessionChanged += (_, e) => OnUi(() => OnSession(e));
        Loc.Instance.PropertyChanged += (_, _) => OnUi(UpdateStatuses);
        Rebuild();
    }

    public GameCatalog Catalog => _catalog;

    /// <summary>The tab shown in the detail; a new game opens on "Profile", the first decision it needs (F5).</summary>
    [ObservableProperty]
    public partial int SelectedTabIndex { get; set; }

    [ObservableProperty]
    public partial StatusKind HeadStatusKind { get; private set; }

    [ObservableProperty]
    public partial string HeadStatusText { get; private set; } = string.Empty;

    /// <summary>The selected game's session runs: the primary reads "Running" and is off (R-FLOW-4).</summary>
    [ObservableProperty]
    public partial bool IsSelectedRunning { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool CanPlay { get; private set; }

    /// <summary>"Play", or "Running" while the session runs – the button says why it is off.</summary>
    [ObservableProperty]
    public partial string PlayLabel { get; private set; } = string.Empty;

    /// <summary>The bar offers the jump to the "Game" tab when the process was not recognised (F5, error row).</summary>
    [ObservableProperty]
    public partial bool DetailOffersGameTab { get; private set; }

    protected override IEnumerable<GameItem> CatalogItems => _catalog.Items;

    protected override string UnnamedText => Loc.Instance["Games_NewName"];

    protected override Task<GameEditorViewModel> CreateEditorAsync(GameItem item) => _dialogs.CreateEditorAsync(item.Game, item.IsNew);

    protected override Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? target) => _dialogs.ConfirmUnsavedAsync(name, target);

    protected override Task<bool> ConfirmDeleteAsync(GameItem item) => _dialogs.ConfirmDeleteAsync(item.Name);

    protected override async Task DeleteStoredAsync(GameItem item)
    {
        await _catalog.DeleteAsync(item.Game.Id, CancellationToken.None);
        GameShortcutIcon.Delete(item.Game.Id, _paths.Icons, Log);
    }

    protected override bool StoredChanged(GameEditorViewModel editor) => !StoredForm.Same(_catalog.Find(editor.Id), editor.Saved);

    protected override void OnEditorLoaded(GameEditorViewModel editor, GameItem item)
    {
        editor.ProcessNotRecognised = _lastEnded.GetValueOrDefault(item.Game.Id)?.Outcome == GameSessionOutcome.NotRecognised;
        if (_openProfileTabAfterLoad)
        {
            _openProfileTabAfterLoad = false;
            SelectedTabIndex = ProfileTab;
        }
    }

    protected override void OnEditorPropertyChanged(GameEditorViewModel editor, string? propertyName)
    {
        if (propertyName == nameof(GameEditorViewModel.Hotkey))
        {
            UpdateHead();
        }
    }

    protected override void ShowDetail(string text, InfoKind kind) => ShowDetail(text, kind, offersGameTab: false);

    /// <summary>Installed games from the picker, several at once; each becomes a saved entry, the first is selected (F5).</summary>
    [RelayCommand]
    private async Task AddInstalledAsync()
    {
        if (!await ConfirmLeaveAsync())
        {
            return;
        }

        AddedGames result = await _dialogs.AddInstalledAsync();
        if (result.Added.Count > 0)
        {
            SelectAfterRebuild(result.Added[0].Id);
            _openProfileTabAfterLoad = true;
            Rebuild();
            ShowStatus(result.Added.Count == 1
                ? Loc.Format("Games_AddedOne", result.Added[0].Name)
                : Loc.Format("Games_Added", result.Added.Count));
        }

        if (result.Skipped > 0)
        {
            ShowDetail(Loc.Format("Games_AddedSkipped", result.Skipped), InfoKind.Info);
        }
    }

    /// <summary>A program from the file dialog becomes an unsaved entry at the top of the list, named after the file.</summary>
    [RelayCommand]
    private async Task AddProgramAsync()
    {
        if (!await ConfirmLeaveAsync() || _dialogs.PickExecutable() is not { } path)
        {
            return;
        }

        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = Core.Profiles.ProfileEditing.UniqueName(Path.GetFileNameWithoutExtension(path), _catalog.Games.Select(g => g.Name)),
            Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = path },
        };
        _openProfileTabAfterLoad = true;
        BeginNew(new GameItem(game) { IsNew = true });
        Log.Information("New game started from {Path}", path);
    }

    /// <summary>"Choose…" on the Game tab: an installed game or a program for the entry being edited.</summary>
    [RelayCommand]
    private async Task PickGameAsync()
    {
        if (Editor is not { } editor || await _dialogs.PickGameAsync() is not { } picked)
        {
            return;
        }

        if (picked.Installed is { } installed)
        {
            editor.SetLaunch(installed);
        }
        else if (picked.ExecutablePath is { } path)
        {
            editor.SetExecutable(path);
        }
    }

    [RelayCommand]
    private void CaptureWindows()
    {
        if (Editor is { } editor && _dialogs.CaptureWindows(editor.WindowLayout) is { } captured)
        {
            editor.WindowLayout = captured;
        }
    }

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (SelectedItem is { IsNew: false } item)
        {
            _sessions.Start(item.Game);
        }
    }

    [RelayCommand]
    private void ShowGameTab() => SelectedTabIndex = 0;

    /// <summary>A new name: the own desktop shortcut follows, links and Stream Deck keys cannot (v4 finding U-12).</summary>
    protected override void OnSaved(GameEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.RenamedFrom is not { } oldName)
        {
            return;
        }

        bool moved = DesktopShortcuts.FollowGameRename(oldName, editor.Name, Log);
        ShowDetail(Loc.Format(moved ? "Detail_RenamedShortcut" : "Detail_Renamed", oldName), InfoKind.Info);
    }

    /// <summary>
    /// A desktop shortcut that starts the whole session. Unlike a profile's it carries the game's own name and icon –
    /// it stands next to the game's other shortcuts and should look like one, not like a RigShift setting.
    /// </summary>
    [RelayCommand]
    private void CreateShortcut()
    {
        if (SelectedItem is not { IsNew: false } item || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        string title = DesktopShortcuts.GameTitle(item.Name);
        string file = DesktopShortcuts.GameFile(item.Name);

        // No icon found is the fallback, not the error case: the shortcut then shows the RigShift symbol. A found one
        // gets the RigShift card behind it; if that cannot be drawn, the game's plain icon still beats the symbol.
        string? icon = Windows.Games.GameIconSource.Find(item.Game.Launch, Log);
        if (icon is not null)
        {
            icon = GameShortcutIcon.Create(item.Game.Id, icon, _paths.Icons, Log) ?? icon;
        }

        try
        {
            Windows.Shell.ShortcutWriter.Create(
                file,
                executable,
                DesktopShortcuts.GameArguments(item.Name),
                Loc.Format("Shortcut_GameDescription", item.Name),
                icon);
            Log.Information("Shortcut {File} created for game {Game} with icon {Icon}", file, item.Name, icon ?? "(RigShift)");
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            Log.Error(ex, "Shortcut {File} could not be created", file);
            ShowDetail(UserMessages.Describe(ex), InfoKind.Error);
        }
    }

    /// <summary>A session started or ended: list and head follow; the result lands in the bar of the selected game.</summary>
    private void OnSession(GameSessionEvent session)
    {
        if (!session.IsRunning)
        {
            _lastEnded[session.GameId] = session;
        }

        foreach (GameItem item in Items.Where(i => i.Game.Id == session.GameId))
        {
            item.IsRunning = session.IsRunning;
            item.StatusText = session.IsRunning ? Loc.Instance["Game_Running"] : session.Status;
        }

        UpdateStatuses();
        if (SelectedItem?.Game.Id != session.GameId)
        {
            return;
        }

        if (Editor is { } editor && session.Outcome == GameSessionOutcome.NotRecognised)
        {
            editor.ProcessNotRecognised = true;
        }

        if (!session.IsRunning && session.Status is { Length: > 0 } status)
        {
            ShowDetail(status, session.Failed ? InfoKind.Error : InfoKind.Info, offersGameTab: session.Outcome == GameSessionOutcome.NotRecognised);
        }
    }

    protected override void UpdateStatuses()
    {
        foreach (GameItem item in Items)
        {
            item.IsRunning = !item.IsNew && _sessions.IsRunning(item.Game.Id);
            (StatusKind kind, string text) = StatusOf(item);
            item.SetStatus(kind, text);
        }

        UpdateHead();
    }

    /// <summary>"Running since 20:14", "Ready · last today 21:40 · ended", "Process is learned on the first start", red for a failure.</summary>
    private (StatusKind Kind, string Text) StatusOf(GameItem item)
    {
        if (item.IsNew)
        {
            return (StatusKind.Neutral, Loc.Instance["List_Unsaved"]);
        }

        if (item.IsRunning)
        {
            string since = _sessions.RunningSince(item.Game.Id) is { } start ? Loc.Format("Game_RunningSince", Clock(start)) : Loc.Instance["Game_Running"];
            return (StatusKind.Accent, since);
        }

        if (_lastEnded.TryGetValue(item.Game.Id, out GameSessionEvent? last))
        {
            if (last.Failed)
            {
                return (StatusKind.Error, last.Outcome == GameSessionOutcome.NotRecognised
                    ? Loc.Instance["Game_StatusNotRecognised"]
                    : last.Status ?? Loc.Instance["GameOutcome_" + last.Outcome]);
            }

            if (last.Outcome == GameSessionOutcome.Ended)
            {
                return (StatusKind.Ok, Loc.Format("Game_LastEnded", Clock(last.At)));
            }
        }

        if (item.Game.Launch.KnownProcessName() is null)
        {
            return (StatusKind.Warn, Loc.Instance["Game_ProcessUnknownStatus"]);
        }

        return (StatusKind.Ok, Loc.Instance["List_Ready"]);
    }

    /// <summary>"20:14" today, "14.09. 20:31" on another day.</summary>
    private static string Clock(DateTimeOffset at)
    {
        DateTime local = at.ToLocalTime().DateTime;
        return local.Date == DateTime.Today
            ? local.ToString("t", Loc.Instance.Culture)
            : local.ToString("g", Loc.Instance.Culture);
    }

    protected override void UpdateHead()
    {
        GameItem? item = SelectedItem;
        IsSelectedRunning = item is { IsNew: false, IsRunning: true };
        PlayLabel = IsSelectedRunning ? Loc.Instance["Game_Running"] : Loc.Instance["Game_Play"];
        if (item is null || Editor is null)
        {
            HeadStatusKind = StatusKind.Neutral;
            HeadStatusText = string.Empty;
            CanPlay = false;
            return;
        }

        CanPlay = !item.IsNew && !item.IsRunning && !Editor.IsDirty && Editor.ProblemCount == 0;
        (StatusKind kind, string text) = StatusOf(item);
        if (item.IsRunning)
        {
            string profile = item.Game.ProfileId is { } id && _catalog.ProfileNameOf(id) is { } name ? " · " + name : string.Empty;
            HeadStatusKind = kind;
            HeadStatusText = text + profile;
            return;
        }

        if (Editor.IsDirty && !item.IsNew)
        {
            kind = StatusKind.Neutral;
            text = Loc.Instance["SaveBar_Unsaved"];
        }

        HeadStatusKind = kind;
        HeadStatusText = text;
    }

    private void ShowDetail(string text, InfoKind kind, bool offersGameTab)
    {
        DetailOffersGameTab = offersGameTab;
        base.ShowDetail(text, kind);
    }
}
