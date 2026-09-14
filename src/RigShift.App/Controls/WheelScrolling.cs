using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace RigShift.App.Controls;

/// <summary>
/// Hands the mouse wheel to the next outer scroll viewer when the innermost one under the pointer cannot scroll further.
/// </summary>
/// <remarks>
/// A WPF <see cref="ScrollViewer"/> marks every wheel event as handled, even when it has nothing to scroll. Text boxes
/// carry such a scroll viewer in their template, so the wheel over a name field in the editor or on the displays page
/// did nothing. Pages additionally set <c>ScrollViewer.CanContentScroll="False"</c>: otherwise WPF-UI's
/// NavigationView wraps them in its own scroll viewer, the page's scroll viewer gets unlimited height and swallows the
/// wheel, and only the outer scroll bar at the right edge reacted (hardware test of 1.3.1).
/// </remarks>
public static class WheelScrolling
{
    private static int _registered;

    /// <summary>Registers the class handler for every scroll viewer of the app, once.</summary>
    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(OnPreviewMouseWheel));
        }
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // The preview tunnels through every scroll viewer on the way; only the innermost one decides.
        if (e.Handled || sender is not ScrollViewer viewer || NearestScrollViewer(e.OriginalSource as DependencyObject) != viewer
            || CanScroll(viewer, e.Delta))
        {
            return;
        }

        ScrollViewer? target = viewer;
        do
        {
            target = NearestScrollViewer(ParentOf(target));
        }
        while (target is not null && !CanScroll(target, e.Delta));

        if (target is null)
        {
            return;
        }

        e.Handled = true;
        target.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = viewer });
    }

    private static bool CanScroll(ScrollViewer viewer, int delta) =>
        delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight;

    private static ScrollViewer? NearestScrollViewer(DependencyObject? element)
    {
        while (element is not null and not ScrollViewer)
        {
            element = ParentOf(element);
        }

        return element as ScrollViewer;
    }

    /// <summary>Visual parent; stops at a popup root, so an open drop-down never scrolls the page behind it.</summary>
    private static DependencyObject? ParentOf(DependencyObject element) =>
        element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
}
