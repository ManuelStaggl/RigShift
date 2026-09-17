using System.Windows.Controls;
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
    }
}
