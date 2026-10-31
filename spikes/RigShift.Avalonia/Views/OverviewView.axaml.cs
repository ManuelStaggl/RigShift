using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.ViewModels;

namespace RigShift.Avalonia.Views;

/// <summary>The overview: a switch with one position per profile, the current layout under it, games as a launcher.</summary>
public sealed partial class OverviewView : UserControl
{
    public OverviewView()
    {
        ViewModel = App.Provider.GetRequiredService<OverviewViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        // The view model lives as long as the app; the view only while it is shown.
        AttachedToVisualTree += (_, _) =>
        {
            ViewModel.PropertyChanged += OnChanged;
            ViewModel.Coordinator.PropertyChanged += OnChanged;
            ViewModel.RefreshCommand.Execute(null);
            ShowState();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnChanged;
            ViewModel.Coordinator.PropertyChanged -= OnChanged;
        };
    }

    public OverviewViewModel ViewModel { get; }

    private ProfileItem? Active => ViewModel.Catalog.Items.FirstOrDefault(i => i.IsActive);

    private void OnChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.UIThread.Post(ShowState);

    private void ShowState()
    {
        EmptyState.IsVisible = !ViewModel.HasProfiles;
        ProfileItem? active = Active;
        NowText.Text = ViewModel.ActiveName;
        NowMeta.Text = active is null ? null : string.Join(" · ", active.DisplayLines);
        ReapplyButton.IsVisible = active is not null;

        // Action bar: the active profile and the switch to the next one.
        ProfileItem? next = ViewModel.Catalog.Items.FirstOrDefault(i => !i.IsActive);
        BarName.Text = active?.Name ?? ViewModel.ActiveName;
        BarIcon.Data = Glyphs.Profile(active?.IconKey);
        BarSwitch.Tag = next;
        BarSwitch.IsVisible = next is not null;
        BarSwitchText.Text = next is null ? null : $"Switch to {next.Name}";
        global::Avalonia.Automation.AutomationProperties.SetName(BarSwitch, BarSwitchText.Text);

        bool switching = ViewModel.Coordinator.SwitchingProfile is not null;
        NowLine.IsVisible = !switching;
        SwitchingLine.IsVisible = switching;
        SwitchingText.Text = switching ? Loc.Format("Tray_SwitchingTo", ViewModel.Coordinator.SwitchingProfile!.Name) : string.Empty;
    }

    // Bound like the WinUI page; the spike never clicks them (and its display configurator cannot apply).
    private void OnSwitch(object? sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(((Control)sender!).Tag as ProfileItem);

    private void OnPlay(object? sender, RoutedEventArgs e) => ViewModel.PlayCommand.Execute(((Control)sender!).Tag as GameItem);

    private void OnReapply(object? sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(Active);

    private void OnAllGames(object? sender, RoutedEventArgs e) => (TopLevel.GetTopLevel(this) as MainWindow)?.Navigate(RigShift.App.Services.AppPage.Games);

    private void OnActions(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e) =>
        (TopLevel.GetTopLevel(this) as MainWindow)?.OpenPalette();
}
