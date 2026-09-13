using System.Windows.Controls;
using RigShift.App.ViewModels;

namespace RigShift.App.Views.Pages;

public partial class ProfilesPage : Page
{
    public ProfilesPage(ProfilesViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
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

public partial class DiagnosticsPage : Page
{
    public DiagnosticsPage(DiagnosticsViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        InitializeComponent();
        Loaded += (_, _) => viewModel.RefreshCommand.Execute(null);
    }
}
