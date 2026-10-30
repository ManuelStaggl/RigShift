using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using RigShift.App.ViewModels;

namespace RigShift.UI.Pages;

/// <summary>Overview prototype B: the profiles as the positions of one switch, games as a launcher grid (#91).</summary>
public sealed partial class OverviewSwitchPage : Page
{
    public OverviewSwitchPage()
    {
        InitializeComponent();
    }

    public TrayPopupViewModel ViewModel { get; private set; } = null!;

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        ViewModel = (TrayPopupViewModel)e.Parameter;
        ViewModel.Catalog.PropertyChanged += (_, _) => ShowActive();
        ShowActive();
        bool switching = OverviewPrototype.ShowsSwitching;
        Steps.Visibility = switching ? Visibility.Visible : Visibility.Collapsed;
        IdleLine.Visibility = switching ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ShowActive()
    {
        ProfileItem? active = OverviewPrototype.Active(ViewModel);
        ActiveSummary.Text = active is null ? string.Empty : string.Join(" · ", active.DisplayLines);
    }

    private void OnSwitch(object sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(((FrameworkElement)sender).Tag as ProfileItem);

    private void OnPlay(object sender, RoutedEventArgs e) => ViewModel.PlayCommand.Execute(((FrameworkElement)sender).Tag as GameItem);

    private void OnReapply(object sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(OverviewPrototype.Active(ViewModel));
}

/// <summary>Preview helpers of the overview prototype.</summary>
internal static class OverviewPrototype
{
    /// <summary>RIGSHIFT_PREVIEW_SWITCHING=1 shows the switch steps with demo content, for screenshots.</summary>
    public static bool ShowsSwitching => Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_SWITCHING") == "1";

    // shortcut: on this server no demo profile matches the displays; the first one is marked active for screenshots.
    public static ProfileItem? Active(TrayPopupViewModel viewModel)
    {
        ProfileItem? active = viewModel.Catalog.Items.FirstOrDefault(i => i.IsActive);
        if (active is null && viewModel.Catalog.Items.Count > 0)
        {
            active = viewModel.Catalog.Items[0];
            active.IsActive = true;
        }

        return active;
    }
}
