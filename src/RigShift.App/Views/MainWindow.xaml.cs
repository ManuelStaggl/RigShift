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
/// keeps running in the tray. Ctrl+1…5 jump to the five pages (R-NAV-5).
/// </summary>
public partial class MainWindow : FluentWindow
{
    private readonly IServiceProvider _services;
    private readonly IAppShell _shell;
    private readonly Dictionary<Type, ListBoxItem> _navItems = [];
    private Type _page = typeof(OverviewPage);
    private bool _syncingNav;

    public MainWindow(IServiceProvider services, IAppShell shell, SwitchCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _services = services;
        _shell = shell;
        InitializeComponent();

        AddNav(NavTop, typeof(OverviewPage), SymbolRegular.Home16, "Nav_Overview", Key.D1);
        AddNav(NavTop, typeof(ProfilesPage), SymbolRegular.Desktop16, "Nav_Profiles", Key.D2);
        AddNav(NavTop, typeof(GamesPage), SymbolRegular.Games16, "Nav_Games", Key.D3);
        AddNav(NavBottom, typeof(SettingsPage), SymbolRegular.Settings16, "Nav_Settings", Key.D4);
        AddNav(NavBottom, typeof(AboutPage), SymbolRegular.QuestionCircle16, "Nav_About", Key.D5);

        Progress.SetBinding(VisibilityProperty, new Binding(nameof(SwitchCoordinator.IsSwitching))
        {
            Source = coordinator,
            Converter = (IValueConverter)Application.Current.Resources["BoolToVisibility"],
        });

        Loaded += (_, _) => ShowPage(_page);
    }

    public void ShowPage(Type? page)
    {
        _page = page ?? _page;
        if (!IsLoaded)
        {
            return;
        }

        if (_navItems.TryGetValue(_page, out ListBoxItem? item) && !item.IsSelected)
        {
            _syncingNav = true;
            NavTop.SelectedItem = NavTop.Items.Contains(item) ? item : null;
            NavBottom.SelectedItem = NavBottom.Items.Contains(item) ? item : null;
            _syncingNav = false;
        }

        if (PageHost.Content?.GetType() != _page)
        {
            PageHost.Navigate(_services.GetRequiredService(_page));
            while (PageHost.CanGoBack)
            {
                PageHost.RemoveBackEntry();
            }
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Dark caption and frame from DWM without the system theme watcher – RigShift has one theme.
        WindowBackgroundManager.UpdateBackground(this, ApplicationTheme.Dark, WindowBackdropType.None);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_shell.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    private void AddNav(ListBox list, Type page, SymbolRegular symbol, string textKey, Key shortcut)
    {
        var text = new System.Windows.Controls.TextBlock { VerticalAlignment = VerticalAlignment.Center };
        text.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new Binding("[" + textKey + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay });
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new SymbolIcon { Symbol = symbol, FontSize = 16, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(text);
        var item = new ListBoxItem { Content = content, Tag = page };
        AutomationProperties.SetName(item, Loc.Instance[textKey]);
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
