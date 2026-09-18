using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace RigShift.App.Controls;

/// <summary>
/// Opens a button's context menu below it on click and Enter, not only on right click. A menu at the right end of a
/// row (the "…" button) aligns its right edge with the button, so it never runs past the window.
/// </summary>
public static class MenuButton
{
    private const double Gap = 4;

    public static void Open(FrameworkElement button, object? dataContext, bool alignRight = false)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (button.ContextMenu is not { } menu)
        {
            return;
        }

        menu.PlacementTarget = button;
        menu.DataContext = dataContext;
        menu.Placement = PlacementMode.Bottom;
        menu.VerticalOffset = Gap;
        menu.HorizontalOffset = 0;
        if (alignRight)
        {
            // The width is known only once the menu is open; moving it then is not visible, it is still fading in.
            menu.Opened += AlignRight;
        }

        menu.IsOpen = true;
    }

    private static void AlignRight(object sender, RoutedEventArgs e)
    {
        var menu = (ContextMenu)sender;
        menu.Opened -= AlignRight;
        if (menu.PlacementTarget is FrameworkElement button)
        {
            menu.HorizontalOffset = button.ActualWidth - menu.ActualWidth;
        }
    }
}
