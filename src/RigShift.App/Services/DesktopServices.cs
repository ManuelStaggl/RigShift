using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.App.Views.Pages;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;

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
    private static readonly Dictionary<string, BitmapImage> Icons = [];

    private readonly TaskbarIcon _icon;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IAppShell _shell;
    private readonly ILogger _log;

    public TrayIconService(
        ProfileCatalog catalog, SwitchCoordinator coordinator, TrayPopupView popup, TrayPopupViewModel popupViewModel, IAppShell shell, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(popupViewModel);
        ArgumentNullException.ThrowIfNull(log);

        _catalog = catalog;
        _coordinator = coordinator;
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
        catalog.Changed += (_, _) => Refresh();
        Loc.Instance.PropertyChanged += (_, _) => Refresh();
        coordinator.SwitchCompleted += (_, record) => Notify(SwitchMessages.ForNotification(record));
        coordinator.BusyRejected += (_, _) => Notify(("RigShift", Loc.Instance["Result_Busy"], NotificationIcon.Info));
    }

    public void Start()
    {
        Refresh();
        // No efficiency mode: it throttles timers, and the confirmation countdown must stay accurate.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _log.Information("Tray icon created");
    }

    public void Dispose() => _icon.Dispose();

    private void Notify((string Title, string Text, NotificationIcon Icon) message) =>
        _icon.ShowNotification(message.Title, message.Text, message.Icon);

    private void Refresh()
    {
        Profile? active = _catalog.ActiveProfile;
        _icon.IconSource = IconFor(active);
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
        menu.Items.Add(Command(Loc.Instance["Tray_Open"], () => _shell.ShowMainWindow()));
        menu.Items.Add(Command(Loc.Instance["Tray_Settings"], () => _shell.ShowMainWindow(typeof(SettingsPage))));
        menu.Items.Add(new Separator());
        menu.Items.Add(Command(Loc.Instance["Tray_Exit"], _shell.Quit));
        _icon.ContextMenu = menu;
    }

    private static MenuItem Command(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    private static BitmapImage IconFor(Profile? profile)
    {
        string name = profile?.Icon?.ToLowerInvariant() switch
        {
            "desk" => "desk",
            _ => "rig",
        };

        if (!Icons.TryGetValue(name, out BitmapImage? image))
        {
            image = new BitmapImage(new Uri("pack://application:,,,/Assets/" + name + ".ico", UriKind.Absolute));
            image.Freeze();
            Icons[name] = image;
        }

        return image;
    }
}
