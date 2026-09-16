using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App.ViewModels;

public sealed partial class GamesViewModel : ObservableObject
{
    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;
    private readonly GameDialogs _dialogs;
    private readonly ILogger _log;

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

        sessions.SessionChanged += (_, e) => Follow(e);
        catalog.Changed += (_, _) => RestoreRunningFlags();
    }

    public GameCatalog Catalog => _catalog;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial Wpf.Ui.Controls.InfoBarSeverity StatusSeverity { get; set; }

    public async Task LoadAsync()
    {
        await _catalog.ReloadAsync(CancellationToken.None);
        RestoreRunningFlags();
    }

    [RelayCommand]
    private async Task AddAsync()
    {
        if (await _dialogs.CreateAsync() is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private async Task EditAsync(GameItem? item)
    {
        if (item is not null && await _dialogs.EditAsync(item.Game) is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private void Play(GameItem? item)
    {
        if (item is not null)
        {
            _sessions.Start(item.Game);
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync(GameItem? item)
    {
        if (item is null)
        {
            return;
        }

        GameEntry copy = item.Game with
        {
            Id = Guid.NewGuid(),
            Name = Core.Profiles.ProfileEditing.UniqueName(
                Loc.Format("Profile_CopyName", item.Name), _catalog.Games.Select(g => g.Name)),
        };
        await RunStoreActionAsync(() => _catalog.SaveAsync(copy, CancellationToken.None), Loc.Format("Status_Duplicated", copy.Name));
    }

    /// <summary>
    /// A desktop shortcut that starts the whole session. Unlike a profile's it carries the game's own name and icon –
    /// it stands next to the game's other shortcuts and should look like one, not like a RigShift setting.
    /// </summary>
    [RelayCommand]
    private void CreateShortcut(GameItem? item)
    {
        if (item is null || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        string title = Windows.Shell.ShortcutWriter.SafeFileName(item.Name);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), title + ".lnk");

        // No icon found is the fallback, not the error case: the shortcut then shows the RigShift symbol.
        string? icon = Windows.Games.GameExecutable.Find(item.Game.Launch, _log);
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
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException or IOException)
        {
            _log.Error(ex, "Shortcut {File} could not be created", file);
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(GameItem? item)
    {
        if (item is not null && await GameDialogs.ConfirmDeleteAsync(item.Name))
        {
            await RunStoreActionAsync(() => _catalog.DeleteAsync(item.Game.Id, CancellationToken.None), Loc.Format("Status_Deleted", item.Name));
        }
    }

    private void Follow(GameSessionEvent session)
    {
        foreach (GameItem item in _catalog.Items.Where(i => i.Game.Id == session.GameId))
        {
            item.IsRunning = session.IsRunning;
            item.StatusText = session.IsRunning ? Loc.Instance["Game_Running"] : session.Status;
        }

        if (!session.IsRunning && session.Status is { Length: > 0 } status)
        {
            ShowStatus(status, Wpf.Ui.Controls.InfoBarSeverity.Informational);
        }
    }

    /// <summary>After a reload the cards are new objects; a session that still runs has to be marked again.</summary>
    private void RestoreRunningFlags()
    {
        foreach (GameItem item in _catalog.Items)
        {
            item.IsRunning = _sessions.IsRunning(item.Game.Id);
            if (item.IsRunning)
            {
                item.StatusText = Loc.Instance["Game_Running"];
            }
        }
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
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    private void ShowStatus(string message, Wpf.Ui.Controls.InfoBarSeverity severity = Wpf.Ui.Controls.InfoBarSeverity.Success)
    {
        // Closed first: after the user closed the bar with its X, setting true again must be a change (finding I-02).
        IsStatusOpen = false;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }
}
