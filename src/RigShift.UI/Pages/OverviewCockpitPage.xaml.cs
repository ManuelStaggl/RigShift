using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigShift.App.Localization;
using RigShift.App.ViewModels;

namespace RigShift.UI.Pages;

/// <summary>Overview style prototype "Cockpit": a switch with one position per profile, the current layout under it, games as a launcher.</summary>
public sealed partial class OverviewCockpitPage : Page
{
    public OverviewCockpitPage()
    {
        ViewModel = App.Provider.GetRequiredService<OverviewViewModel>();
        InitializeComponent();
        // The view model lives as long as the app; the page only while it is shown.
        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnChanged;
            ViewModel.Coordinator.PropertyChanged += OnChanged;
            ViewModel.RefreshCommand.Execute(null);
            ShowState();
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnChanged;
            ViewModel.Coordinator.PropertyChanged -= OnChanged;
        };
    }

    public OverviewViewModel ViewModel { get; }

    private ProfileItem? Active => ViewModel.Catalog.Items.FirstOrDefault(i => i.IsActive);

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => ShowState();

    private void ShowState()
    {
#if DEBUG
        PreviewActive();
#endif
        EmptyState.Visibility = ViewModel.HasProfiles ? Visibility.Collapsed : Visibility.Visible;
        NowText.Text = Loc.Instance["Overview_Now"] + " · " + ViewModel.ActiveName;
        ReapplyButton.Visibility = Active is null ? Visibility.Collapsed : Visibility.Visible;

        bool switching = ViewModel.Coordinator.SwitchingProfile is not null;
        NowLine.Visibility = switching ? Visibility.Collapsed : Visibility.Visible;
        SwitchingLine.Visibility = switching ? Visibility.Visible : Visibility.Collapsed;
        SwitchingText.Text = switching ? Loc.Format("Tray_SwitchingTo", ViewModel.Coordinator.SwitchingProfile!.Name) : string.Empty;
    }

#if DEBUG
    /// <summary>RIGSHIFT_PREVIEW_ACTIVE=&lt;profile&gt; lights a demo profile for screenshots; no demo profile matches this server's displays.</summary>
    private void PreviewActive()
    {
        string? name = Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_ACTIVE");
        if (name is not null && Active is null && ViewModel.Catalog.Items.FirstOrDefault(i => i.Name == name) is { } item)
        {
            item.IsActive = true;
            ViewModel.ActiveName = item.Name;
        }
    }
#endif

    private void OnSwitch(object sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(((FrameworkElement)sender).Tag as ProfileItem);

    private void OnPlay(object sender, RoutedEventArgs e) => ViewModel.PlayCommand.Execute(((FrameworkElement)sender).Tag as GameItem);

    private void OnReapply(object sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(Active);
}
