using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Win32;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Windows.Ui;
using Serilog;
using Wpf.Ui.Appearance;

namespace RigShift.App.Services;

/// <summary>Tray icon: left click opens the profile popup, right click (or the keyboard menu key) the context menu.</summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Brand charcoal: the symbol color on a light taskbar (white on a dark one).</summary>
    private static readonly Color LightTaskbarStroke = Color.FromRgb(0x0F, 0x17, 0x2A);

    private readonly TaskbarIcon _icon;
    private System.Drawing.Icon? _trayIcon;
    private (string? Key, int Size, bool LightTaskbar, Color Color) _iconState;
    private ApplicationTheme? _popupTheme;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly ProfilesViewModel _profiles;
    private readonly IAppShell _shell;
    private readonly UpdateService _updates;
    private readonly AutomationService _automation;
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private static readonly TimeSpan ErrorNotificationInterval = TimeSpan.FromSeconds(30);

    private readonly ILogger _log;
    /// <summary>What a click on the last notification does; null when it does nothing.</summary>
    private Action? _notificationClick;
    private long? _lastErrorNotification;

    public TrayIconService(
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        TrayPopupView popup,
        TrayPopupViewModel popupViewModel,
        ProfilesViewModel profiles,
        IAppShell shell,
        UpdateService updates,
        HotkeyService hotkeys,
        AutomationService automation,
        GameCatalog games,
        GameSessionService sessions,
        SwitchOptions switchOptions,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(switchOptions);
        ArgumentNullException.ThrowIfNull(hotkeys);
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(games);
        ArgumentNullException.ThrowIfNull(sessions);
        _automation = automation;
        _games = games;
        _sessions = sessions;
        automation.Changed += (_, _) => OnUi(RebuildMenu);

        // A game that runs is not startable again, and a game added on the page belongs in the menu right away.
        games.Changed += (_, _) => OnUi(RebuildMenu);
        sessions.SessionChanged += (_, _) => OnUi(RebuildMenu);
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(popupViewModel);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _coordinator = coordinator;
        _profiles = profiles;
        _shell = shell;
        _log = log.ForContext<TrayIconService>();
        _icon = new TaskbarIcon
        {
            PopupActivation = PopupActivationMode.LeftClick,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
            TrayPopup = popup,
            ContextMenu = new ContextMenu(),
        };

        popupViewModel.CloseRequested += (_, _) => _icon.CloseTrayPopup();

        // Application resource changes only reach Application.Windows; the popup is no window and kept half of the old
        // theme (M5: light background, white rows after switching Windows light → dark). Touching its own resources
        // makes WPF re-resolve every DynamicResource below it.
        ApplicationThemeManager.Changed += (theme, _) => popup.Dispatcher.InvokeAsync(() =>
        {
            // Each window reports the same change once (finding R-01); high contrast colors can change within the theme.
            if (theme == _popupTheme && theme != ApplicationTheme.HighContrast)
            {
                return;
            }

            _popupTheme = theme;
            var nudge = new ResourceDictionary();
            popup.Resources.MergedDictionaries.Add(nudge);
            popup.Resources.MergedDictionaries.Remove(nudge);
            _log.Debug("Tray popup resources refreshed for theme {Theme}", theme);
        });
        catalog.ProfilesChanged += (_, _) => OnUi(Refresh);
        catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileCatalog.ActiveProfile))
            {
                OnUi(Refresh);
            }
        };
        Loc.Instance.PropertyChanged += (_, _) => OnUi(Refresh);
        coordinator.SwitchCompleted += (_, record) => OnUi(() =>
        {
            profiles.ShowSwitchResult(record);

            // A failed or blocked switch leads to About & help: recent switches, the log folder and the diagnostic report.
            Notify(SwitchMessages.ForNotification(record), opensAbout: record.Outcome is SwitchOutcome.Failed or SwitchOutcome.Blocked);
        });
        coordinator.AppsCompleted += (_, record) => OnUi(() =>
        {
            if (SwitchMessages.ForAppsNotification(record) is { } apps)
            {
                Notify(apps);
            }
        });
        coordinator.WaitingForDisplays += (_, names) => OnUi(() => Notify((
            Loc.Instance["Result_WaitingTitle"],
            Loc.Format("Result_WaitingText", string.Join(", ", names), (int)switchOptions.MissingDisplayWaitBudget.TotalSeconds),
            NotificationIcon.Info)));
        coordinator.BusyRejected += (_, _) => OnUi(() => Notify(("RigShift", Loc.Instance["Result_Busy"], NotificationIcon.Info)));
        _updates = updates;
        updates.UpdateReady += (_, version) => OnUi(() => Notify(("RigShift", Loc.Format("Update_Ready", version), NotificationIcon.Info), opensAbout: true));
        updates.UpdateAvailable += (_, version) => OnUi(() => Notify(("RigShift", Loc.Format("Update_Available", version), NotificationIcon.Info), opensAbout: true));
        updates.StateChanged += (_, _) => OnUi(RebuildMenu);
        hotkeys.RegistrationFailed += (_, names) =>
            OnUi(() => Notify(("RigShift", Loc.Format("Hotkey_Failed", string.Join(", ", names)), NotificationIcon.Warning)));

        // Update notifications and switch problems lead to the about page (install the update, recent switches, log).
        _icon.TrayBalloonTipClicked += (_, _) =>
        {
            Action? click = _notificationClick;
            _notificationClick = null;
            click?.Invoke();
        };
        _icon.TrayBalloonTipClosed += (_, _) => _notificationClick = null;

        // Windows restored a profile's displays by itself: a click applies audio, apps and the rest (finding HW-15).
        coordinator.RestoredByWindows += (_, restored) => OnUi(() => Notify(
            (Loc.Format("Restored_Title", restored.Name), Loc.Format("Restored_Text", restored.Name), NotificationIcon.Info),
            async () =>
            {
                if (_catalog.ActiveProfile?.Id != restored.Id)
                {
                    _log.Information("Rest of {Profile} not applied: {Active} is active now", restored.Name, _catalog.ActiveProfile?.Name ?? "(none)");
                    return;
                }

                await _coordinator.SwitchAsync(restored, new SwitchRequest { KeepDisplays = true });
            }));
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching))
            {
                OnUi(UpdateIcon);
            }
        };

        // Taskbar theme, contrast theme and DPI can change at any time.
        SystemEvents.UserPreferenceChanged += OnSystemChanged;
        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
    }

    public void Start()
    {
        Refresh();
        // No efficiency mode: it throttles timers, and the confirmation countdown must stay accurate.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        if (_icon.TrayPopupResolved is { } popup)
        {
            popup.Opened += OnTrayPopupOpened;
        }
        else
        {
            _log.Warning("Tray popup not resolved; its placement is left to H.NotifyIcon");
        }

        _log.Information("Tray icon created");
    }

    /// <summary>
    /// H.NotifyIcon converts the cursor position with the DPI factor captured at startup, so on a monitor with another
    /// scale the popup lands far from the tray (M5, G9 at 125 % after starting on a 150 % desk). Re-place it in physical
    /// pixels; once more after layout, because moving to another monitor rescales the popup.
    /// </summary>
    private void OnTrayPopupOpened(object? sender, EventArgs e)
    {
        if (sender is not System.Windows.Controls.Primitives.Popup { Child: { } child })
        {
            return;
        }

        void Place(string pass)
        {
            if (PresentationSource.FromVisual(child) is HwndSource source)
            {
                (int X, int Y)? placed = NativeWindow.PlaceNearCursor(source.Handle);
                _log.Debug("Tray popup placed ({Pass}) at {Position}", pass, placed?.ToString() ?? "failed");
            }
        }

        Place("opened");
        _icon.Dispatcher.InvokeAsync(() => Place("after layout"), DispatcherPriority.Loaded);
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemChanged;
        SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
        _icon.Dispose();
        _trayIcon?.Dispose();
    }

    // SystemEvents may raise on its own thread.
    private void OnSystemChanged(object? sender, EventArgs e) => _icon.Dispatcher.InvokeAsync(UpdateIcon);

    /// <summary>
    /// The active profile's symbol in the taskbar's color, rendered for the current DPI; the RigShift symbol while
    /// switching or when no profile with a known symbol is active.
    /// </summary>
    private void UpdateIcon()
    {
        int size = NativeWindow.SmallIconSize();
        bool highContrast = SystemParameters.HighContrast;
        bool lightTaskbar = highContrast ? BrandTheme.IsLight(SystemColors.WindowColor) : NativeWindow.IsTaskbarLight();
        string? key = _coordinator.IsSwitching ? null : ProfileIcons.Normalize(_catalog.ActiveProfile?.Icon);

        System.Drawing.Icon icon;
        Color color = highContrast ? SystemColors.WindowTextColor : lightTaskbar ? LightTaskbarStroke : Colors.White;

        // Windows raises preference and display events in bursts (seven redraws after an RDP reconnect, finding R-01).
        var state = (key, size, lightTaskbar, color);
        if (_trayIcon is not null && state == _iconState)
        {
            return;
        }

        _iconState = state;
        if (ProfileIconRenderer.Render(key, size, color) is { } bitmap)
        {
            icon = ProfileIconRenderer.ToIcon(bitmap);
        }
        else
        {
            // Tray file names name the taskbar background: "light" holds the dark symbol.
            var uri = new Uri("pack://application:,,,/Assets/Brand/rigshift-tray-" + (lightTaskbar ? "light" : "dark") + ".ico", UriKind.Absolute);
            using Stream stream = Application.GetResourceStream(uri).Stream;
            icon = new System.Drawing.Icon(stream, size, size);
        }

        System.Drawing.Icon? previous = _trayIcon;
        _trayIcon = icon;
        _icon.Icon = icon;
        previous?.Dispose();
        _log.Debug("Tray icon {Symbol} at {Size} px, light taskbar {LightTaskbar}", key ?? "rigshift", size, lightTaskbar);
    }

    /// <summary>
    /// An unexpected error in the UI, once per <see cref="ErrorNotificationInterval"/> so a failing timer cannot flood the
    /// tray. Before, such errors only reached the log and a button simply seemed to do nothing (analysis finding I-07).
    /// </summary>
    public void ShowUnexpectedError(string message)
    {
        long now = Environment.TickCount64;
        if (_lastErrorNotification is { } last && now - last < (long)ErrorNotificationInterval.TotalMilliseconds)
        {
            return;
        }

        _lastErrorNotification = now;
        Notify(("RigShift", Loc.Format("Status_Error", message), NotificationIcon.Error), opensAbout: true);
    }

    private void Notify((string Title, string Text, NotificationIcon Icon) message, bool opensAbout = false) =>
        Notify(message, opensAbout ? () => _shell.ShowMainWindow(typeof(AboutPage)) : null);

    private void Notify((string Title, string Text, NotificationIcon Icon) message, Action? click)
    {
        _notificationClick = click;
        _icon.ShowNotification(message.Title, message.Text, message.Icon);
    }

    /// <summary>
    /// Runs a tray update on the UI thread. Everything below touches WPF objects – the tray icon, its menu, its
    /// notifications – but the events that ask for it arrive from wherever their sender runs: a game session applies
    /// its profile on a background thread, and the switch coordinator raises its events there. Calling straight into
    /// WPF from that thread throws "the calling thread cannot access this object", which aborted the game start.
    /// </summary>
    private void OnUi(Action update)
    {
        if (_icon.Dispatcher.CheckAccess())
        {
            update();
        }
        else
        {
            _icon.Dispatcher.InvokeAsync(update);
        }
    }

    private void Refresh()
    {
        Profile? active = _catalog.ActiveProfile;
        UpdateIcon();
        _icon.ToolTipText = "RigShift – " + (active?.Name ?? Loc.Instance["Tray_ActiveNone"]);
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        ContextMenu menu = _icon.ContextMenu ?? new ContextMenu();
        menu.Items.Clear();
        foreach (TrayMenuEntry entry in TrayMenuModel.Build(MenuState()))
        {
            menu.Items.Add(entry.IsSeparator ? new Separator() : MenuItemFor(entry));
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

    private MenuItem MenuItemFor(TrayMenuEntry entry)
    {
        var item = new MenuItem
        {
            Header = entry.Header,
            InputGestureText = entry.Gesture,
            IsEnabled = entry.IsEnabled,
            IsCheckable = entry.IsCheckable,
            IsChecked = entry.IsChecked,
        };

        // A check box line has toggled itself by the time Click arrives.
        item.Click += async (_, _) => await RunAsync(entry, item.IsChecked);
        return item;
    }

    private async Task RunAsync(TrayMenuEntry entry, bool isChecked)
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
                _shell.ShowMainWindow(typeof(ProfilesPage));
                _profiles.NewFromCurrentCommand.Execute(null);
                break;
            case TrayMenuCommand.Open:
                _shell.ShowMainWindow();
                break;
            case TrayMenuCommand.Settings:
                _shell.ShowMainWindow(typeof(SettingsPage));
                break;
            case TrayMenuCommand.PauseAutomation:
                await _automation.SetPausedAsync(isChecked);
                break;
            case TrayMenuCommand.InstallUpdate:
                await _updates.InstallNowAsync();
                break;
            case TrayMenuCommand.Exit:
                _shell.QuitByUser();
                break;
        }
    }
}
