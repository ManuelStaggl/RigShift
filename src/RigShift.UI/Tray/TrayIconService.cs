using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.UI.Tray;

/// <summary>
/// Tray icon through Shell_NotifyIcon (H.NotifyIcon's UI-free core): left click opens the popup, right click the
/// context menu from <see cref="TrayMenuModel"/>, notifications for switches and updates.
/// </summary>
internal sealed class TrayIconService : IDisposable
{
    // A fixed id per program path: Windows keeps the user's "show in the taskbar" choice for it across starts.
    private readonly TrayIconWithContextMenu _icon = new(TrayIcon.CreateUniqueGuidForProcessPath(Environment.ProcessPath ?? "RigShift"));
    private readonly DispatcherQueue _ui = DispatcherQueue.GetForCurrentThread();
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly Func<ProfilesViewModel> _profiles;
    private readonly IAppShell _shell;
    private readonly UpdateService _updates;
    private readonly AutomationService _automation;
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private readonly TrayPopupViewModel _popupViewModel;
    private readonly ILogger _log;
    private TrayPopupWindow? _popup;
    private System.Drawing.Icon? _trayIcon;
    private (int Size, bool LightTaskbar) _iconState;

    /// <summary>What a click on the last notification does; null when it does nothing.</summary>
    private Action? _notificationClick;

    public TrayIconService(
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        TrayPopupViewModel popupViewModel,
        Func<ProfilesViewModel> profiles,
        IAppShell shell,
        UpdateService updates,
        AutomationService automation,
        GameCatalog games,
        GameSessionService sessions,
        SwitchOptions switchOptions,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(switchOptions);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _coordinator = coordinator;
        _popupViewModel = popupViewModel;
        _profiles = profiles;
        _shell = shell;
        _updates = updates;
        _automation = automation;
        _games = games;
        _sessions = sessions;
        _log = log.ForContext<TrayIconService>();

        // The same events as the WPF tray; that one goes with the WPF app (#99).
        automation.Changed += (_, _) => OnUi(RebuildMenu);
        games.Changed += (_, _) => OnUi(RebuildMenu);
        sessions.SessionChanged += (_, _) => OnUi(RebuildMenu);
        updates.StateChanged += (_, _) => OnUi(RebuildMenu);
        catalog.ProfilesChanged += (_, _) => OnUi(Refresh);
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileCatalog.ActiveProfile))
            {
                OnUi(Refresh);
            }
        };
        Loc.Instance.PropertyChanged += (_, _) => OnUi(Refresh);

        // A failed or blocked switch leads to help: recent switches, the log folder and the diagnostic report.
        coordinator.SwitchCompleted += (_, record) => OnUi(() =>
            Notify(SwitchMessages.ForNotification(record), opensHelp: record.Outcome is SwitchOutcome.Failed or SwitchOutcome.Blocked));
        coordinator.FollowUpCompleted += (_, record) => OnUi(() =>
        {
            if (SwitchMessages.ForFollowUpNotification(record) is { } followUp)
            {
                Notify(followUp);
            }
        });
        coordinator.WaitingForDisplays += (_, names) => OnUi(() => Notify((
            Loc.Instance["Result_WaitingTitle"],
            Loc.Format("Result_WaitingText", string.Join(", ", names), (int)switchOptions.MissingDisplayWaitBudget.TotalSeconds),
            NoticeLevel.Info)));
        coordinator.BusyRejected += (_, _) => OnUi(() => Notify(("RigShift", Loc.Instance["Result_Busy"], NoticeLevel.Info)));
        coordinator.AllDisplaysOnCompleted += (_, report) => OnUi(() => Notify(SwitchMessages.ForAllDisplaysOn(report)));
        updates.UpdateReady += (_, version) => OnUi(() => Notify(("RigShift", Loc.Format("Update_Ready", version), NoticeLevel.Info), opensHelp: true));
        updates.UpdateAvailable += (_, version) => OnUi(() => Notify(("RigShift", Loc.Format("Update_Available", version), NoticeLevel.Info), opensHelp: true));

        // Windows restored a profile's displays by itself: a click applies audio, apps and the rest.
        coordinator.RestoredByWindows += (_, restored) => OnUi(() => Notify(
            (Loc.Format("Restored_Title", restored.Name), Loc.Format("Restored_Text", restored.Name), NoticeLevel.Info),
            async () =>
            {
                if (_catalog.ActiveProfile?.Id != restored.Id)
                {
                    _log.Information("Rest of {Profile} not applied: {Active} is active now", restored.Name, _catalog.ActiveProfile?.Name ?? "(none)");
                    return;
                }

                await _coordinator.SwitchAsync(restored, new SwitchRequest { KeepDisplays = true });
            }));

        popupViewModel.CloseRequested += (_, _) => _popup?.Close();

        // The icon's own window raises these; everything that follows touches WinUI or the services.
        _icon.MessageWindow.MouseEventReceived += (_, e) =>
        {
            switch (e.MouseEvent)
            {
                case MouseEvent.IconLeftMouseUp:
                    OnUi(ShowPopup);
                    break;
                case MouseEvent.BalloonToolTipClicked:
                    OnUi(() =>
                    {
                        Action? click = _notificationClick;
                        _notificationClick = null;
                        click?.Invoke();
                    });
                    break;
            }
        };
        // A restarted Explorer has an empty tray; the core library leaves adding the icon again to its user.
        _icon.MessageWindow.TaskbarCreated += (_, _) => OnUi(() =>
        {
            _icon.TryRemove();
            _icon.Create();
            _log.Information("Explorer restarted, tray icon added again");
        });

        // Taskbar theme and DPI can change at any time; SystemEvents raises on its own thread.
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
    }

    public void Start()
    {
        Refresh();
        _icon.Create();
        _log.Information("Tray icon created");
    }

    /// <summary>"RigShift keeps running in the tray", on the first close of the window.</summary>
    public void ShowKeepsRunningHint() => OnUi(() => Notify(("RigShift", Loc.Instance["Tray_KeepsRunning"], NoticeLevel.Info), click: null));

    /// <summary>A link, Stream Deck key or shortcut that could not run: its caller has no console to say why.</summary>
    public void ShowRefusedCommand(string text) => OnUi(() => Notify(("RigShift", text, NoticeLevel.Warning), click: null));

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        _icon.Dispose();
        _trayIcon?.Dispose();
    }

    private void OnSystemChanged(object? sender, EventArgs e) => OnUi(UpdateIcon);

    private void OnUi(Action update)
    {
        if (_ui.HasThreadAccess)
        {
            update();
        }
        else if (!_ui.TryEnqueue(() => update()))
        {
            _log.Debug("Tray update dropped, the UI thread has ended");
        }
    }

    public void ShowPopup()
    {
        // A new window each time: closing it on deactivation is simpler than hiding, and it is small.
        _popup?.Close();
        _popup = new TrayPopupWindow(_popupViewModel);
        _popup.Closed += (sender, _) =>
        {
            if (ReferenceEquals(sender, _popup))
            {
                _popup = null;
            }
        };
        _popup.ShowNearCursor();
    }

    private void Refresh()
    {
        UpdateIcon();
        _icon.UpdateToolTip("RigShift – " + (_catalog.ActiveProfile?.Name ?? Loc.Instance["Tray_ActiveNone"]));
        RebuildMenu();
    }

    /// <summary>The RigShift symbol in the taskbar's colour, at the current DPI.</summary>
    private void UpdateIcon()
    {
        int size = NativeWindow.SmallIconSize();
        bool lightTaskbar = NativeWindow.IsTaskbarLight();
        if (_trayIcon is not null && _iconState == (size, lightTaskbar))
        {
            return;
        }

        _iconState = (size, lightTaskbar);

        // Tray file names name the taskbar background: "light" holds the dark symbol.
        string file = Path.Combine(AppContext.BaseDirectory, "Assets", "rigshift-tray-" + (lightTaskbar ? "light" : "dark") + ".ico");
        System.Drawing.Icon? previous = _trayIcon;
        _trayIcon = new System.Drawing.Icon(file, size, size);
        _icon.UpdateIcon(_trayIcon.Handle);
        previous?.Dispose();
        _log.Debug("Tray icon at {Size} px, light taskbar {LightTaskbar}", size, lightTaskbar);
    }

    private void RebuildMenu()
    {
        var menu = new PopupMenu();
        foreach (TrayMenuEntry entry in TrayMenuModel.Build(MenuState()))
        {
            menu.Items.Add(entry.IsSeparator ? new PopupMenuSeparator() : MenuItemFor(entry));
        }

        _icon.ContextMenu = menu;
    }

    private TrayMenuState MenuState() => new()
    {
        Profiles = _catalog.Items,
        Games = _games.Items,
        RunningGames = _games.Items.Select(i => i.Game.Id).Where(_sessions.IsRunning).ToHashSet(),
        HasAutomation = _automation.Rules.Count > 0,
        AutomationPaused = _automation.IsPaused,
        UpdateVersion = _updates.State is UpdateState.Ready or UpdateState.Available ? _updates.TargetVersion : null,
        CanInstallUpdate = _updates.CanInstallNow,
    };

    private PopupMenuItem MenuItemFor(TrayMenuEntry entry)
    {
        // A tab puts the gesture in the menu's shortcut column.
        string text = entry.Gesture.Length > 0 ? entry.Header + "\t" + entry.Gesture : entry.Header;
        var item = new PopupMenuItem(text, (_, _) => OnUi(async () => await RunAsync(entry)))
        {
            Enabled = entry.IsEnabled,
            Checked = entry.IsChecked,
        };
        return item;
    }

    private async Task RunAsync(TrayMenuEntry entry)
    {
        switch (entry.Command)
        {
            case TrayMenuCommand.SwitchProfile when entry.Profile is { } profile:
                await _coordinator.SwitchAsync(profile);
                break;
            case TrayMenuCommand.PlayGame when entry.Game is { } game:
                _sessions.Start(game);
                break;
            case TrayMenuCommand.SaveCurrent:
                _shell.ShowMainWindow(AppPage.Profiles);
                _profiles().NewFromCurrentCommand.Execute(null);
                break;
            case TrayMenuCommand.Open:
                _shell.ShowMainWindow();
                break;
            case TrayMenuCommand.Settings:
                _shell.ShowMainWindow(AppPage.Settings);
                break;
            case TrayMenuCommand.PauseAutomation:
                // A native menu line does not toggle itself: the new state is the opposite of the shown one.
                await _automation.SetPausedAsync(!entry.IsChecked);
                break;
            case TrayMenuCommand.InstallUpdate:
                await _updates.InstallNowAsync();
                break;
            case TrayMenuCommand.Exit:
                _shell.QuitByUser();
                break;
        }
    }

    private void Notify((string Title, string Text, NoticeLevel Icon) message, bool opensHelp = false) =>
        Notify(message, opensHelp ? () => _shell.ShowMainWindow(AppPage.Help) : null);

    private void Notify((string Title, string Text, NoticeLevel Icon) message, Action? click)
    {
        _notificationClick = click;
        _icon.ShowNotification(message.Title, message.Text, message.Icon switch
        {
            NoticeLevel.Warning => NotificationIcon.Warning,
            NoticeLevel.Error => NotificationIcon.Error,
            _ => NotificationIcon.Info,
        });
    }
}
