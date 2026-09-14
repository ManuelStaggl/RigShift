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
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;
using Wpf.Ui.Appearance;

namespace RigShift.App.Services;

/// <summary><see cref="ISwitchConfirmation"/> as a countdown window on the new primary display.</summary>
public sealed class WpfSwitchConfirmation : ISwitchConfirmation
{
    public Task<ConfirmationResult> ConfirmAsync(Profile profile, TimeSpan timeout, CancellationToken cancellationToken) =>
        Application.Current.Dispatcher
            .InvokeAsync(() => ConfirmationWindow.ShowAsync(profile, timeout, cancellationToken))
            .Task
            .Unwrap();
}

/// <summary>Hidden top-level window that receives <c>WM_DISPLAYCHANGE</c> (message-only windows do not get broadcasts).</summary>
public sealed class DisplayChangeWatcher : IDisposable
{
    private readonly HwndSource _source;
    private readonly DispatcherTimer _debounce;

    public DisplayChangeWatcher()
    {
        _source = new HwndSource(new HwndSourceParameters("RigShift.DisplayChangeWatcher") { Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);

        // A topology change sends a burst of messages; react once when it has settled.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            DisplaysChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? DisplaysChanged;

    public void Dispose()
    {
        _debounce.Stop();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmDisplayChange)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        return 0;
    }
}

/// <summary>Tray icon: left click opens the profile popup, right click (or the keyboard menu key) the context menu.</summary>
public sealed class TrayIconService : IDisposable
{
    /// <summary>Brand charcoal: the symbol color on a light taskbar (white on a dark one).</summary>
    private static readonly Color LightTaskbarStroke = Color.FromRgb(0x0F, 0x17, 0x2A);

    private readonly TaskbarIcon _icon;
    private System.Drawing.Icon? _trayIcon;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly ProfilesViewModel _profiles;
    private readonly IAppShell _shell;
    private readonly UpdateService _updates;
    private readonly ILogger _log;
    private bool _updateNotificationShown;

    public TrayIconService(
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        TrayPopupView popup,
        TrayPopupViewModel popupViewModel,
        ProfilesViewModel profiles,
        IAppShell shell,
        UpdateService updates,
        ILogger log)
    {
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
            var nudge = new ResourceDictionary();
            popup.Resources.MergedDictionaries.Add(nudge);
            popup.Resources.MergedDictionaries.Remove(nudge);
            _log.Debug("Tray popup resources refreshed for theme {Theme}", theme);
        });
        catalog.Changed += (_, _) => Refresh();
        Loc.Instance.PropertyChanged += (_, _) => Refresh();
        coordinator.SwitchCompleted += (_, record) => Notify(SwitchMessages.ForNotification(record));
        coordinator.BusyRejected += (_, _) => Notify(("RigShift", Loc.Instance["Result_Busy"], NotificationIcon.Info));
        _updates = updates;
        updates.UpdateReady += (_, version) =>
        {
            Notify(("RigShift", Loc.Format("Update_Ready", version), NotificationIcon.Info));
            _updateNotificationShown = true;
        };
        updates.StateChanged += (_, _) => RebuildMenu();

        // The update notification leads to the settings, where the update can be installed right away.
        _icon.TrayBalloonTipClicked += (_, _) =>
        {
            if (_updateNotificationShown)
            {
                _updateNotificationShown = false;
                _shell.ShowMainWindow(typeof(SettingsPage));
            }
        };
        _icon.TrayBalloonTipClosed += (_, _) => _updateNotificationShown = false;
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching))
            {
                UpdateIcon();
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

    private void Notify((string Title, string Text, NotificationIcon Icon) message)
    {
        _updateNotificationShown = false;
        _icon.ShowNotification(message.Title, message.Text, message.Icon);
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

        foreach (ProfileItem item in _catalog.Items)
        {
            var entry = new MenuItem { Header = item.Name, IsChecked = item.IsActive };
            Profile profile = item.Profile;
            entry.Click += async (_, _) => await _coordinator.SwitchAsync(profile);
            menu.Items.Add(entry);
        }

        if (_catalog.Items.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Loc.Instance["Tray_NoProfiles"], IsEnabled = false });
        }

        menu.Items.Add(new Separator());
        menu.Items.Add(Command(Loc.Instance["Tray_SaveCurrent"], () =>
        {
            _shell.ShowMainWindow(typeof(ProfilesPage));
            _profiles.SaveCurrentCommand.Execute(null);
        }));
        menu.Items.Add(Command(Loc.Instance["Tray_Open"], () => _shell.ShowMainWindow()));
        menu.Items.Add(Command(Loc.Instance["Tray_Settings"], () => _shell.ShowMainWindow(typeof(SettingsPage))));
        menu.Items.Add(new Separator());
        if (_updates.State == UpdateState.Ready && _updates.TargetVersion is { } version)
        {
            MenuItem restart = Command(Loc.Format("Tray_RestartToUpdate", version), _updates.RestartAndInstall);
            restart.IsEnabled = _updates.CanRestart;
            menu.Items.Add(restart);
        }

        menu.Items.Add(Command(Loc.Instance["Tray_Exit"], _shell.Quit));
        _icon.ContextMenu = menu;
    }

    private static MenuItem Command(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
