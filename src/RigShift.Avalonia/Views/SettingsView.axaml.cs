using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.ViewModels;

namespace RigShift.Avalonia.Views;

/// <summary>The settings in three groups (switching, the app, devices) as rows on cards.</summary>
public sealed partial class SettingsView : UserControl
{
    public SettingsView()
    {
        ViewModel = App.Provider.GetRequiredService<SettingsViewModel>();
        DataContext = ViewModel;
        InitializeComponent();
        // Read again on every visit: rules and devices change on other pages.
        AttachedToVisualTree += (_, _) => ViewModel.Load();
    }

    public SettingsViewModel ViewModel { get; }

    private async void OnDeviceNameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: UsbNameCard card })
        {
            await card.SaveNameAsync();
        }
    }

    private async void OnDeviceNameKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not Control { DataContext: UsbNameCard card })
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
