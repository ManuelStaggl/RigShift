using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using RigShift.App.Controls;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class WheelScrollingTests
{
    [Fact]
    public void WheelOverTextBox_InsideScrollViewer_ScrollsTheOuterViewer() => RunSta(() =>
    {
        var textBox = new TextBox { Text = "Left" };
        ScrollViewer page = Page(textBox);

        bool handled = Wheel(ScrollViewerIn(textBox));

        handled.ShouldBeTrue();
        page.UpdateLayout();
        page.VerticalOffset.ShouldBeGreaterThan(0);
    });

    [Fact]
    public void WheelOverInnerViewer_ThatCanScroll_IsLeftToIt() => RunSta(() =>
    {
        var inner = new ScrollViewer { Height = 50, Content = new Border { Height = 500 } };
        ScrollViewer page = Page(inner);

        bool handled = Wheel(inner);

        handled.ShouldBeFalse();
        page.VerticalOffset.ShouldBe(0);
    });

    private static ScrollViewer Page(UIElement first)
    {
        WheelScrolling.Register();
        var content = new StackPanel();
        content.Children.Add(first);
        content.Children.Add(new Border { Height = 1000 });
        var page = new ScrollViewer { Width = 200, Height = 100, Content = content };
        page.Measure(new Size(200, 100));
        page.Arrange(new Rect(0, 0, 200, 100));
        page.UpdateLayout();
        return page;
    }

    /// <summary>What the input manager does for a wheel turn down: the preview first, the bubbling event if unhandled.</summary>
    private static bool Wheel(UIElement source)
    {
        var preview = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        source.RaiseEvent(preview);
        return preview.Handled;
    }

    private static ScrollViewer ScrollViewerIn(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            try
            {
                return ScrollViewerIn(child);
            }
            catch (InvalidOperationException)
            {
                // Not in this branch.
            }
        }

        throw new InvalidOperationException("No scroll viewer in the template.");
    }

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
