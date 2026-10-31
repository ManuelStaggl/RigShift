using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>
/// Captures where the helper windows sit. Everything open is listed and the user ticks what belongs to this game –
/// capturing the whole desktop would drag Explorer and the browser into the layout as well.
/// </summary>
public partial class WindowCaptureWindow : FluentWindow
{
    private readonly WindowCaptureViewModel _viewModel;

    private WindowCaptureWindow(WindowCaptureViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        TextScale.Apply(this);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this);
    }

    public WindowLayout? Captured { get; private set; }

    /// <returns>The captured layout, or <c>null</c> if cancelled.</returns>
    public static WindowLayout? Capture(Window? owner, GameDialogs dialogs, WindowLayout? current)
    {
        ArgumentNullException.ThrowIfNull(dialogs);

        var viewModel = new WindowCaptureViewModel(dialogs.OpenWindows(), current);
        var window = new WindowCaptureWindow(viewModel);
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        return window.ShowDialog() == true ? window.Captured : null;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        Captured = _viewModel.ToLayout();
        DialogResult = true;
    }
}
