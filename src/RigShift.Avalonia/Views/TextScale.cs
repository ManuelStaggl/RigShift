using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using RigShift.App.Services;
using Serilog;

namespace RigShift.Avalonia.Views;

/// <summary>
/// Windows' accessibility setting "Text size" (100–225 %) for the dialogs and the tray popup, which Avalonia does not
/// follow on its own: the content below the title bar is scaled and the window's sizes follow.
/// Read when a window is created; a change shows in the next window opened.
/// </summary>
internal static class TextScale
{
    // The system title bar of a dialog sits on top of its client height.
    private const double Caption = 40;

    /// <summary>The primary monitor's work area in device-independent pixels; unbounded before a screen is known.</summary>
    public static (double Width, double Height) Room(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return window.Screens.Primary is { } screen
            ? (screen.WorkingArea.Width / screen.Scaling, screen.WorkingArea.Height / screen.Scaling)
            : (double.PositiveInfinity, double.PositiveInfinity);
    }

    /// <summary>Call right after <c>InitializeComponent</c> (or once the content is set), before the window is shown.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        (double roomWidth, double roomHeight) = Room(window);
        if (window.WindowDecorations != WindowDecorations.None)
        {
            roomHeight -= Caption;
        }

        double factor = TextScaleSetting.Factor(TextScaleSetting.Read(), roomWidth, roomHeight, window.MinWidth, window.MinHeight);
        if (factor <= 1 || window.Content is not Control content)
        {
            return;
        }

        // Taken out of the window first: a control has one parent.
        window.Content = null;
        Control scaled = new LayoutTransformControl { LayoutTransform = new ScaleTransform(factor, factor), Child = content };
        // A window that grows with its content scrolls once it reaches the screen's height, so its buttons stay reachable.
        if (window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
        {
            scaled = new ScrollViewer { Content = scaled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        window.Content = scaled;
        window.MinWidth *= factor;
        window.MinHeight *= factor;
        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width * factor, roomWidth);
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height * factor, roomHeight);
        }

        window.MaxHeight = Math.Min(window.MaxHeight * factor, roomHeight);
        Log.Information("Text size {Factor:0.##}x applied to {Window}", factor, window.GetType().Name);
    }
}
