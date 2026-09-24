using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using RigShift.App.Services;
using RigShift.App.ViewModels;

namespace RigShift.App.Views.Pages;

public partial class ProfilesPage : Page
{
    private readonly ProfilesViewModel _viewModel;

    public ProfilesPage(ProfilesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusNameRequested += (_, _) => FocusName();
        PreviewKeyDown += OnPagePreviewKeyDown;
        Loaded += (_, _) => viewModel.PageShown();
    }

    /// <summary>F2 edits the name (R-NAV-5).</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnMoreClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnIconClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel.Editor);

    private void OnAddDeviceClick(object sender, RoutedEventArgs e)
    {
        var button = (FrameworkElement)sender;
        Controls.MenuButton.Open(button, button.DataContext);
    }
}

public partial class SettingsPage : Page
{
    public SettingsPage(SettingsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Load();
    }

    private async void OnDeviceNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: UsbNameCard card })
        {
            await card.SaveNameAsync();
        }
    }

    /// <summary>Enter saves, Esc puts the saved name back (F6).</summary>
    private async void OnDeviceNameKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: UsbNameCard card })
        {
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            card.CustomName = card.SavedName ?? string.Empty;
        }
    }
}

public partial class OverviewPage : Page
{
    public OverviewPage(OverviewViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }

    private async void OnNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DisplayCard card })
        {
            await card.SaveNameAsync();
        }
    }

    /// <summary>Enter saves, Esc puts the saved name back (F6).</summary>
    private async void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: DisplayCard card })
        {
            return;
        }

        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            card.CustomName = card.SavedName ?? string.Empty;
        }
    }
}

/// <summary>The field of view page; the displays are read when the page is shown.</summary>
public partial class FovPage : Page
{
    public FovPage(FovViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}

public partial class AboutPage : Page
{
    public AboutPage(AboutViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}

public partial class GamesPage : Page
{
    private readonly GamesViewModel _viewModel;

    public GamesPage(GamesViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        viewModel.FocusNameRequested += (_, _) => FocusName();
        PreviewKeyDown += OnPagePreviewKeyDown;
        Loaded += (_, _) => viewModel.PageShown();
    }

    /// <summary>F2 edits the name (R-NAV-5).</summary>
    private void OnPagePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.HasSelection)
        {
            e.Handled = true;
            FocusName();
        }
    }

    private void FocusName()
    {
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNewClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnMoreClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);

    private void OnIconClick(object sender, RoutedEventArgs e) => Controls.MenuButton.Open((FrameworkElement)sender, _viewModel.Editor);
}
