using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>
/// Windows' accessibility setting "Text size" (Settings → Accessibility, 100–225 %). WPF ignores it, and the
/// app's type sizes are fixed tokens, so a window grows as a whole instead: its content below the title bar is
/// scaled and its sizes follow. Read when a window is created; a change shows in the next window opened.
/// </summary>
internal static class TextScale
{
    private const string KeyPath = @"Software\Microsoft\Accessibility";
    private const string ValueName = "TextScaleFactor";
    private const double Largest = 2.25;

    /// <summary>
    /// The factor for a window: the setting, never below 1, and no larger than lets the window's smallest size
    /// still fit the screen – a layout cut off at the edge helps nobody who needs large text.
    /// </summary>
    internal static double Factor(int? percent, double roomWidth, double roomHeight, double minWidth, double minHeight)
    {
        double factor = Math.Clamp((percent ?? 100) / 100d, 1, Largest);
        if (minWidth > 0)
        {
            factor = Math.Min(factor, roomWidth / minWidth);
        }

        if (minHeight > 0)
        {
            factor = Math.Min(factor, roomHeight / minHeight);
        }

        return Math.Max(1, factor);
    }

    /// <summary>Call right after <c>InitializeComponent</c>, so the start position already counts with the grown size.</summary>
    /// <returns>The factor in use; a window that compares its own width divides by it.</returns>
    public static double Apply(FluentWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);

        Rect room = SystemParameters.WorkArea;
        double factor = Factor(Setting(), room.Width, room.Height, window.MinWidth, window.MinHeight);
        if (factor <= 1)
        {
            return 1;
        }

        // The title bar stays as Windows draws its own: caption buttons keep their place and their hit areas.
        if (window.Content is Panel root)
        {
            foreach (UIElement child in root.Children)
            {
                if (child is FrameworkElement element and not TitleBar)
                {
                    element.LayoutTransform = Frozen(factor);
                }
            }
        }

        window.MinWidth *= factor;
        window.MinHeight *= factor;
        if (!double.IsNaN(window.Width))
        {
            window.Width = Math.Min(window.Width * factor, room.Width);
        }

        if (!double.IsNaN(window.Height))
        {
            window.Height = Math.Min(window.Height * factor, room.Height);
        }

        if (!double.IsInfinity(window.MaxHeight))
        {
            window.MaxHeight = Math.Min(window.MaxHeight * factor, room.Height);
        }

        return factor;
    }

    /// <summary>For a surface that is no window of its own (the tray popup).</summary>
    public static void Apply(FrameworkElement surface)
    {
        ArgumentNullException.ThrowIfNull(surface);

        Rect room = SystemParameters.WorkArea;
        double factor = Factor(Setting(), room.Width, room.Height, 0, 0);
        if (factor > 1)
        {
            surface.LayoutTransform = Frozen(factor);
        }
    }

    private static ScaleTransform Frozen(double factor)
    {
        var scale = new ScaleTransform(factor, factor);
        scale.Freeze();
        return scale;
    }

    /// <summary>The Windows "Text size" in percent, <c>null</c> when it was never changed.</summary>
    internal static int? Setting()
    {
#if DEBUG
        // Screenshots of the scaled layout without touching the Windows setting.
        if (int.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TEXTSCALE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int preview))
        {
            return preview;
        }
#endif
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Warning(ex, "Text size setting not readable, windows keep their normal size");
            return null;
        }
    }
}
