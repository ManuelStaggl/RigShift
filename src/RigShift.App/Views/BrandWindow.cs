using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using RigShift.Windows.Ui;
using Serilog;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>Gives a window the brand's chrome. Every <see cref="FluentWindow"/> of the app calls this.</summary>
internal static class BrandWindow
{
    /// <summary>
    /// Dark caption and frame from DWM without the system theme watcher – RigShift has one theme. The call
    /// assigns WPF-UI's own dark grey (#202020) to the window background, so the brand chrome goes back on
    /// afterwards; without it the title bar and the side rail turn grey instead of the deep blue.
    /// </summary>
    /// <param name="window">The window to dress.</param>
    /// <param name="surfaceKey">Brush the window keeps as its background; a dialog uses the page surface.</param>
    public static void ApplyChrome(FluentWindow window, string surfaceKey = "RigShift.Brush.Chrome")
    {
        ArgumentNullException.ThrowIfNull(window);
        WindowBackgroundManager.UpdateBackground(window, ApplicationTheme.Dark, WindowBackdropType.None);
        window.SetResourceReference(Control.BackgroundProperty, surfaceKey);
        window.ContentRendered += KeepOnScreen;
    }

    /// <summary>
    /// Once shown, a window that sticks out of its monitor is pulled back in – with mixed DPI, WPF can open it larger
    /// than a side monitor, with the caption buttons out of reach.
    /// </summary>
    private static void KeepOnScreen(object? sender, EventArgs e)
    {
        var window = (Window)sender!;
        window.ContentRendered -= KeepOnScreen;
        if (window.WindowState != WindowState.Normal)
        {
            return;
        }

        double width = double.IsNaN(window.Width) ? window.ActualWidth : window.Width;
        double height = double.IsNaN(window.Height) ? window.ActualHeight : window.Height;
        if (NativeWindow.FitIntoMonitor(new WindowInteropHelper(window).Handle, width, height) is { } bounds)
        {
            Log.Information("{Window} opened outside its monitor's work area, moved to {Bounds}", window.GetType().Name, bounds);
        }
    }
}
