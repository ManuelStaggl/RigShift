using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The games page: the master list on the left, the selected game's detail (its editor, tabs in session order) on the
/// right (R-NAV-2, F5). A running session marks its game in the list and locks "Play" (R-FLOW-4).
/// </summary>
public sealed partial class GamesViewModel : ObservableObject
{
    private static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(3);

    /// <summary>The tab that is the first decision a new game needs (F5).</summary>
    private const int ProfileTab = 1;

    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;
    private readonly GameDialogs _dialogs;
    private readonly ILogger _log;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Dictionary<Guid, GameSessionEvent> _lastEnded = [];
    private GameItem? _newItem;
    private Guid? _selectAfterRebuild;
    private bool _openProfileTabAfterLoad;
    private bool _rebuildQueued;
    private bool _reverting;
    private CancellationTokenSource? _statusTimer;

    public GamesViewModel(GameCatalog catalog, GameSessionService sessions, GameDialogs dialogs, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _sessions = sessions;
        _dialogs = dialogs;
        _log = log.ForContext<GamesViewModel>();

        catalog.Changed += (_, _) => OnUi(Rebuild);
        catalog.Items.CollectionChanged += OnCatalogItemsChanged;
        sessions.SessionChanged += (_, e) => OnUi(() => OnSession(e));
        Loc.Instance.PropertyChanged += (_, _) => OnUi(UpdateStatuses);
        Rebuild();
    }

    /// <summary>The detail wants the keyboard focus in the name field (F2).</summary>
    public event EventHandler? FocusNameRequested;

    public GameCatalog Catalog => _catalog;

    /// <summary>The saved games, with the unsaved new one on top while there is one.</summary>
    public ObservableCollection<GameItem> Items { get; } = [];

    [ObservableProperty]
    public partial GameItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial GameEditorViewModel? Editor { get; private set; }

    public bool HasSelection => Editor is not null;

    /// <summary>No games at all: the list says so and the detail stays empty (section 6).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

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

    /// <summary>A session result or a store error, as a bar above the tab content; closable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailMessage))]
    public partial string? DetailMessage { get; private set; }

    public bool HasDetailMessage => DetailMessage is not null;

    [ObservableProperty]
    public partial InfoKind DetailKind { get; private set; }

    /// <summary>The bar offers the jump to the "Game" tab when the process was not recognised (F5, error row).</summary>
    [ObservableProperty]
    public partial bool DetailOffersGameTab { get; private set; }

    /// <summary>"'X' saved", for three seconds at the bottom of the detail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>Before the page is left or the window navigates: saves, discards or stays. False: stay.</summary>
    /// <param name="targetName">The game the user picked instead, when the dialog comes from the list.</param>
    public async Task<bool> ConfirmLeaveAsync(string? targetName = null)
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        switch (await ProfileDialogs.ConfirmUnsavedAsync(
            editor.Name.Trim().Length == 0 ? Loc.Instance["Games_NewName"] : editor.Name, targetName))
        {
            case UnsavedChoice.Save:
                return await SaveCoreAsync();
            case UnsavedChoice.Discard:
                DiscardCore();
                return true;
            default:
                return false;
        }
    }

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
            _selectAfterRebuild = result.Added[0].Id;
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
        if (!await ConfirmLeaveAsync() || GameDialogs.PickExecutable() is not { } path)
        {
            return;
        }

        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = Core.Profiles.ProfileEditing.UniqueName(Path.GetFileNameWithoutExtension(path), _catalog.Games.Select(g => g.Name)),
            Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = path },
        };
        _newItem = new GameItem(game) { IsNew = true };
        _newItem.SetStatus(StatusKind.Neutral, Loc.Instance["List_Unsaved"]);
        Items.Insert(0, _newItem);
        IsEmpty = false;
        _openProfileTabAfterLoad = true;
        SelectedItem = _newItem;
        _log.Information("New game started from {Path}", path);
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
    private async Task SaveAsync()
    {
        if (await SaveCoreAsync())
        {
            ShowStatus(Loc.Format("Status_Saved", Editor?.Name ?? string.Empty));
        }
    }

    [RelayCommand]
    private void Discard() => DiscardCore();

    [RelayCommand]
    private void CloseDetailMessage() => DetailMessage = null;

    [RelayCommand]
    private void ShowGameTab() => SelectedTabIndex = 0;

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item || !await GameDialogs.ConfirmDeleteAsync(item.Name))
        {
            return;
        }

        if (item.IsNew)
        {
            DiscardCore();
            return;
        }

        int index = Items.IndexOf(item);
        _selectAfterRebuild = Items.ElementAtOrDefault(index + 1)?.Game.Id ?? Items.ElementAtOrDefault(index - 1)?.Game.Id;
        Editor?.Dispose();
        Editor = null;
        Guid id = item.Game.Id;
        await RunStoreActionAsync(
            async () =>
            {
                await _catalog.DeleteAsync(id, CancellationToken.None);
                GameShortcutIcon.Delete(id, App.Paths.Icons, _log);
            },
            Loc.Format("Status_Deleted", item.Name));
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

        string title = Windows.Shell.ShortcutWriter.SafeFileName(item.Name);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), title + ".lnk");

        // No icon found is the fallback, not the error case: the shortcut then shows the RigShift symbol. A found one
        // gets the RigShift card behind it; if that cannot be drawn, the game's plain icon still beats the symbol.
        string? icon = Windows.Games.GameIconSource.Find(item.Game.Launch, _log);
        if (icon is not null)
        {
            icon = GameShortcutIcon.Create(item.Game.Id, icon, App.Paths.Icons, _log) ?? icon;
        }

        try
        {
            Windows.Shell.ShortcutWriter.Create(
                file,
                executable,
                "play " + Core.Cli.CommandLineArguments.Quote(item.Name),
                Loc.Format("Shortcut_GameDescription", item.Name),
                icon);
            _log.Information("Shortcut {File} created for game {Game} with icon {Icon}", file, item.Name, icon ?? "(RigShift)");
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            _log.Error(ex, "Shortcut {File} could not be created", file);
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    [RelayCommand]
    private void CopyCommand()
    {
        if (Editor is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(Editor.CommandText);
            ShowStatus(Loc.Instance["Trigger_Copied"]);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Clipboard refused the command");
        }
    }

    partial void OnSelectedItemChanged(GameItem? oldValue, GameItem? newValue)
    {
        if (!_reverting)
        {
            _ = SelectAsync(oldValue, newValue);
        }
    }

    private async Task SelectAsync(GameItem? previous, GameItem? next)
    {
        if (previous is not null && previous != next && Editor is { IsDirty: true } && !await ConfirmLeaveAsync(next?.Name))
        {
            _reverting = true;
            SelectedItem = previous;
            _reverting = false;
            return;
        }

        // Confirming may have removed a new item or replaced the list; the selection then is whatever the list shows.
        if (SelectedItem != next)
        {
            return;
        }

        await LoadEditorAsync(next);
    }

    private async Task LoadEditorAsync(GameItem? item)
    {
        Editor?.Dispose();
        DetailMessage = null;
        if (item is null)
        {
            Editor = null;
            UpdateHead();
            return;
        }

        GameEditorViewModel editor = await _dialogs.CreateEditorAsync(item.Game, item.IsNew);
        if (SelectedItem != item)
        {
            editor.Dispose();
            return;
        }

        editor.PropertyChanged += OnEditorChanged;
        editor.ProcessNotRecognised = _lastEnded.GetValueOrDefault(item.Game.Id)?.Outcome == GameSessionOutcome.NotRecognised;
        Editor = editor;
        if (_openProfileTabAfterLoad)
        {
            _openProfileTabAfterLoad = false;
            SelectedTabIndex = ProfileTab;
        }

        UpdateHead();
        if (item.IsNew)
        {
            FocusNameRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GameEditorViewModel.IsDirty) or nameof(GameEditorViewModel.IsNew) or nameof(GameEditorViewModel.Name)
            or nameof(GameEditorViewModel.Hotkey) or nameof(GameEditorViewModel.ErrorMessage))
        {
            if (e.PropertyName == nameof(GameEditorViewModel.ErrorMessage) && Editor?.ErrorMessage is { } error)
            {
                ShowDetail(error, InfoKind.Error);
            }

            UpdateHead();
        }
    }

    private async Task<bool> SaveCoreAsync()
    {
        if (Editor is not { } editor)
        {
            return true;
        }

        bool wasNew = editor.IsNew;
        _selectAfterRebuild = editor.Id;
        if (!await editor.SaveAsync())
        {
            _selectAfterRebuild = null;
            return false;
        }

        if (wasNew)
        {
            _newItem = null;
        }

        // The catalog reloads and fires Changed, which rebuilds the list and keeps this game selected.
        return true;
    }

    /// <summary>Back to the game as saved; a new game disappears from the list.</summary>
    private void DiscardCore()
    {
        if (SelectedItem is not { } item)
        {
            return;
        }

        if (item.IsNew)
        {
            _newItem = null;
            Items.Remove(item);
            IsEmpty = Items.Count == 0;
            _reverting = true;
            SelectedItem = Items.FirstOrDefault();
            _reverting = false;
            _ = LoadEditorAsync(SelectedItem);
            return;
        }

        _ = LoadEditorAsync(item);
    }

    private async Task RunStoreActionAsync(Func<Task> action, string? success)
    {
        try
        {
            await action();
            if (success is not null)
            {
                ShowStatus(success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _log.Error(ex, "Game store action failed");
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    private void OnCatalogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The catalog clears and refills its list item by item; one rebuild after the burst is enough.
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        OnUi(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    /// <summary>The list from the catalog, the new game on top; the selection survives by id.</summary>
    private void Rebuild()
    {
        Guid? keep = _selectAfterRebuild ?? SelectedItem?.Game.Id;
        _selectAfterRebuild = null;
        _reverting = true;
        try
        {
            Items.Clear();
            if (_newItem is not null)
            {
                Items.Add(_newItem);
            }

            foreach (GameItem item in _catalog.Items)
            {
                Items.Add(item);
            }

            IsEmpty = Items.Count == 0;
            UpdateStatuses();
            GameItem? selected = Items.FirstOrDefault(i => i.Game.Id == keep) ?? Items.FirstOrDefault();
            bool sameGame = selected is not null && Editor?.Id == selected.Game.Id && !selected.IsNew && Editor is { IsDirty: false };
            SelectedItem = selected;
            if (!sameGame || Editor is null)
            {
                _ = LoadEditorAsync(selected);
            }
            else
            {
                UpdateHead();
            }
        }
        finally
        {
            _reverting = false;
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

    private void UpdateStatuses()
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

    /// <summary>The head's status line and what the primary action may do right now.</summary>
    private void UpdateHead()
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

    private void ShowDetail(string text, InfoKind kind, bool offersGameTab = false)
    {
        DetailKind = kind;
        DetailOffersGameTab = offersGameTab;
        DetailMessage = text;
    }

    private void ShowStatus(string text)
    {
        _statusTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _statusTimer = timer;
        StatusMessage = text;
        _ = HideStatusAsync(timer);
    }

    private async Task HideStatusAsync(CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(StatusDuration, timer.Token);
            StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            // A newer message took over.
        }
        finally
        {
            if (_statusTimer == timer)
            {
                _statusTimer = null;
            }

            timer.Dispose();
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
