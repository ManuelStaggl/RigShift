using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.Views.Pages;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>
/// Main window: title bar, the navigation rail on the left and one page on the right. Closing only hides it – RigShift
/// keeps running in the tray. Ctrl+1…6 jump to the six pages (R-NAV-5). Below 1200 px the rail keeps only its symbols
/// (N-01); its foot shows the active profile and paused rules, the help entry a dot for an update (N-02).
/// </summary>
public partial class MainWindow : FluentWindow
{
    /// <summary>The rail's entries in order; each page is resolved from the container the first time it is shown.</summary>
    internal static readonly IReadOnlyList<NavEntry> Pages =
    [
        new(typeof(OverviewPage), SymbolRegular.Home16, "Nav_Overview", Key.D1, Bottom: false),
        new(typeof(ProfilesPage), SymbolRegular.Desktop16, "Nav_Profiles", Key.D2, Bottom: false),
        new(typeof(GamesPage), SymbolRegular.Games16, "Nav_Games", Key.D3, Bottom: false),
        new(typeof(FovPage), SymbolRegular.Eye16, "Nav_Fov", Key.D4, Bottom: false),
        new(typeof(SettingsPage), SymbolRegular.Settings16, "Nav_Settings", Key.D5, Bottom: true),
        new(typeof(AboutPage), SymbolRegular.QuestionCircle16, "Nav_About", Key.D6, Bottom: true),
    ];

    private readonly IServiceProvider _pages;
    private readonly IAppShell _shell;
    private readonly Dictionary<Type, ListBoxItem> _navItems = [];
    private readonly List<System.Windows.Controls.TextBlock> _navTexts = [];
    private readonly ProfileCatalog _catalog;
    private readonly AutomationService _automation;
    private readonly UpdateService _updates;
    private readonly ViewModels.ProfilesViewModel _profiles;
    private readonly ViewModels.GamesViewModel _games;
    private System.Windows.Shapes.Ellipse? _updateDot;
    private Type _page = typeof(OverviewPage);
    private bool _syncingNav;

    /// <summary>The user's own choice with the toggle; <c>null</c> follows the window width.</summary>
    private bool? _compactChoice;

    /// <param name="pages">Only for the pages of <see cref="Pages"/>, built when first shown; a test checks that each is registered.</param>
    public MainWindow(
        IServiceProvider pages,
        IAppShell shell,
        SwitchCoordinator coordinator,
        ProfileCatalog catalog,
        AutomationService automation,
        UpdateService updates,
        ViewModels.ProfilesViewModel profiles,
        ViewModels.GamesViewModel games)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _pages = pages;
        _shell = shell;
        _catalog = catalog;
        _automation = automation;
        _updates = updates;
        _profiles = profiles;
        _games = games;
        InitializeComponent();
        _textScale = TextScale.Apply(this);

        // The pages paint square backgrounds; without this they cover the rounded corner of the page surface.
        PageHost.SizeChanged += (_, e) => PageHost.Clip = TopLeftRounded(e.NewSize, 7);
        PageHost.Navigated += (_, e) =>
        {
            if (e.Content is UIElement page)
            {
                Controls.Motion.PlayEnter(page, 12);
            }
        };

        foreach (NavEntry entry in Pages)
        {
            AddNav(entry.Bottom ? NavBottom : NavTop, entry);
        }

        Progress.SetBinding(VisibilityProperty, new Binding(nameof(SwitchCoordinator.IsSwitching))
        {
            Source = coordinator,
            Converter = (IValueConverter)Application.Current.Resources["BoolToVisibility"],
        });

        _catalog.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfileCatalog.ActiveProfile))
            {
                Dispatcher.InvokeAsync(ShowAppState);
            }
        };
        _automation.Changed += (_, _) => Dispatcher.InvokeAsync(ShowAppState);
        _updates.StateChanged += (_, _) => Dispatcher.InvokeAsync(ShowAppState);
        Loc.Instance.PropertyChanged += (_, _) => Dispatcher.InvokeAsync(() => { ShowAppState(); UpdateNavWidth(); });
        SizeChanged += (_, _) => UpdateNavWidth();

        Loaded += (_, _) =>
        {
            ShowAppState();
            UpdateNavWidth();
            ShowPage(_page);
        };
    }

    /// <summary>Windows' text size setting as a factor; the rail's width check counts in layout pixels.</summary>
    private readonly double _textScale;

    private bool IsCompact => _compactChoice ?? ActualWidth / _textScale < 1200;

    private void OnNavToggleClick(object sender, RoutedEventArgs e)
    {
        _compactChoice = !IsCompact;
        UpdateNavWidth();
    }

    /// <summary>Wide rail with names, or 56 px with symbols and the names as tooltips.</summary>
    private void UpdateNavWidth()
    {
        bool compact = IsCompact;
        NavColumn.Width = new GridLength(compact ? 56 : 200);
        foreach (System.Windows.Controls.TextBlock text in _navTexts)
        {
            text.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        }

        foreach (ListBoxItem item in _navItems.Values)
        {
            ToolTipService.SetIsEnabled(item, compact);
        }

        ActiveText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        NavFooter.Padding = compact ? new Thickness(14, 10, 0, 2) : new Thickness(12, 10, 8, 2);
        NavToggleIcon.Symbol = compact ? SymbolRegular.PanelLeftExpand16 : SymbolRegular.PanelLeftContract16;
        string label = Loc.Instance[compact ? "Nav_Expand" : "Nav_Collapse"];
        NavToggle.ToolTip = label;
        AutomationProperties.SetName(NavToggle, label);
        ShowAppState();
    }

    /// <summary>The rail's foot and the update dot: what the app is doing without opening a page.</summary>
    private void ShowAppState()
    {
        string active = _catalog.ActiveProfile?.Name ?? Loc.Instance["Tray_ActiveNone"];
        ActiveText.Text = active;

        // The narrow rail shows only the dot: its meaning comes as a whole sentence, at once, and for a screen reader.
        string state = _catalog.ActiveProfile is { } profile
            ? string.Format(Loc.Instance.UICulture, Loc.Instance["Nav_ActiveProfile"], profile.Name)
            : active;
        NavFooter.ToolTip = state;
        AutomationProperties.SetName(NavFooter, state);
        ActiveDot.Fill = (System.Windows.Media.Brush)FindResource(_catalog.ActiveProfile is null ? "RigShift.Brush.TextDisabled" : "RigShift.Brush.Ok");
        bool paused = _automation.IsPaused;
        PausedIcon.Visibility = paused ? Visibility.Visible : Visibility.Collapsed;

        // The narrow rail has room for one sign: paused rules outrank the profile dot.
        ActiveDot.Visibility = paused && IsCompact ? Visibility.Collapsed : Visibility.Visible;
        PausedIcon.ToolTip = Loc.Instance["Nav_RulesPaused"];
        if (_updateDot is not null)
        {
            _updateDot.Visibility = _updates.State is UpdateState.Available or UpdateState.Ready ? Visibility.Visible : Visibility.Collapsed;
            _updateDot.ToolTip = Loc.Instance["Nav_UpdateReady"];
        }
    }

    public void ShowPage(Type? page) => _ = ShowPageAsync(page).ContinueWith(
        t => Serilog.Log.Error(t.Exception!, "Page {Page} could not be shown", page),
        System.Threading.CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted,
        TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Leaving the profiles or games page with unsaved changes asks first (R-NAV-3); "cancel" keeps the page.</summary>
    private async Task ShowPageAsync(Type? page)
    {
        Type target = page ?? _page;
        if (IsLoaded && PageHost.Content is ProfilesPage && target != typeof(ProfilesPage)
            && !await _profiles.ConfirmLeaveAsync())
        {
            SyncNav(typeof(ProfilesPage));
            return;
        }

        if (IsLoaded && PageHost.Content is GamesPage && target != typeof(GamesPage)
            && !await _games.ConfirmLeaveAsync())
        {
            SyncNav(typeof(GamesPage));
            return;
        }

        _page = target;
        if (!IsLoaded)
        {
            return;
        }

        SyncNav(_page);
        if (PageHost.Content?.GetType() != _page)
        {
            PageHost.Navigate(_pages.GetRequiredService(_page));
            while (PageHost.CanGoBack)
            {
                PageHost.RemoveBackEntry();
            }
        }
    }

    private void SyncNav(Type page)
    {
        if (_navItems.TryGetValue(page, out ListBoxItem? item) && !item.IsSelected)
        {
            _syncingNav = true;
            NavTop.SelectedItem = NavTop.Items.Contains(item) ? item : null;
            NavBottom.SelectedItem = NavBottom.Items.Contains(item) ? item : null;
            _syncingNav = false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this);
    }

    /// <summary>The window was closed by the user and went to the tray; RigShift keeps running.</summary>
    public event EventHandler? HiddenToTray;

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            Hide();
            HiddenToTray?.Invoke(this, EventArgs.Empty);
        }

        base.OnClosing(e);
    }

    /// <summary>A rectangle of <paramref name="size"/> whose top left corner is an arc of <paramref name="radius"/>.</summary>
    private static System.Windows.Media.StreamGeometry TopLeftRounded(Size size, double radius)
    {
        var geometry = new System.Windows.Media.StreamGeometry();
        using (System.Windows.Media.StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(new Point(0, radius), isFilled: true, isClosed: true);
            context.ArcTo(new Point(radius, 0), new Size(radius, radius), 0, isLargeArc: false, System.Windows.Media.SweepDirection.Clockwise, isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(size.Width, 0), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(size.Width, size.Height), isStroked: false, isSmoothJoin: false);
            context.LineTo(new Point(0, size.Height), isStroked: false, isSmoothJoin: false);
        }

        geometry.Freeze();
        return geometry;
    }

    private void AddNav(ListBox list, NavEntry entry)
    {
        (Type page, SymbolRegular symbol, string textKey, Key shortcut, _) = entry;
        var text = new System.Windows.Controls.TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding("[" + textKey + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = new SymbolIcon { Symbol = symbol, FontSize = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(icon);
        content.Children.Add(text);
        var item = new ListBoxItem { Content = content, Tag = page };

        // The chosen page shows its symbol filled (N-03); the name is the tooltip while the rail is narrow.
        icon.SetBinding(SymbolIcon.FilledProperty, new Binding(nameof(ListBoxItem.IsSelected)) { Source = item });
        item.SetBinding(ToolTipProperty, new Binding("[" + textKey + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
        ToolTipService.SetPlacement(item, System.Windows.Controls.Primitives.PlacementMode.Right);
        _navTexts.Add(text);
        if (page == typeof(AboutPage))
        {
            _updateDot = new System.Windows.Shapes.Ellipse
            {
                Width = 7,
                Height = 7,
                Margin = new Thickness(-8, -10, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Fill = (System.Windows.Media.Brush)FindResource("RigShift.Brush.Accent"),
                Visibility = Visibility.Collapsed,
            };
            content.Children.Insert(1, _updateDot);
        }

        // Bound like the text and the tooltip: a name set once kept the old language for screen readers.
        item.SetBinding(AutomationProperties.NameProperty, new Binding("[" + textKey + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
        list.Items.Add(item);
        _navItems[page] = item;
        InputBindings.Add(new KeyBinding(new NavigateCommand(this, page), shortcut, ModifierKeys.Control));
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingNav || sender is not ListBox { SelectedItem: ListBoxItem { Tag: Type page } })
        {
            return;
        }

        // One selection across both lists.
        _syncingNav = true;
        (sender == NavTop ? NavBottom : NavTop).SelectedItem = null;
        _syncingNav = false;
        ShowPage(page);
    }

    /// <summary>One entry of the navigation rail: the page, its symbol and name, and its Ctrl shortcut.</summary>
    /// <param name="Bottom">In the lower group of the rail (settings, help).</param>
    internal sealed record NavEntry(Type Page, SymbolRegular Symbol, string TextKey, Key Shortcut, bool Bottom);

    private sealed class NavigateCommand(MainWindow window, Type page) : ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => window.ShowPage(page);
    }
}
