using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using RigShift.App.ViewModels;

namespace RigShift.App.Views.Pages;

public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }

    /// <summary>The "more" button opens its context menu on click and Enter, not only on right click.</summary>
    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.DataContext = button.DataContext;
            menu.IsOpen = true;
        }
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
}

public partial class DisplaysPage : Page
{
    public DisplaysPage(DisplaysViewModel viewModel)
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

    private async void OnNameKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter && sender is FrameworkElement { DataContext: DisplayCard card })
        {
            e.Handled = true;
            await card.SaveNameAsync();
        }
    }
}

public partial class AutomationPage : Page
{
    public AutomationPage(AutomationViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.Load();
    }

    private void OnBrowseExecutable(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuleCard card })
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Localization.Loc.Instance["App_FileFilter"],
            Title = Localization.Loc.Instance["App_Browse"],
        };

        string current = Environment.ExpandEnvironmentVariables(card.ExecutablePath.Trim().Trim('"'));
        if (System.IO.Path.IsPathFullyQualified(current) && System.IO.Path.GetDirectoryName(current) is { } folder && System.IO.Directory.Exists(folder))
        {
            dialog.InitialDirectory = folder;
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
        {
            card.ExecutablePath = dialog.FileName;
        }
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
