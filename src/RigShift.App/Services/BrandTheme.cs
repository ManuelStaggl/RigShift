using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace RigShift.App.Services;

/// <summary>
/// Follows the Windows theme: the user's accent color for WPF-UI and whether the background is dark, for the tray icon.
/// The brand colors live only in the logo and the tray icon.
/// </summary>
public static class BrandTheme
{
    /// <summary>Whether text and symbols should be light, i.e. the background is dark.</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(ApplicationTheme theme)
    {
        bool highContrast = theme == ApplicationTheme.HighContrast;
        IsDark = highContrast ? !IsLight(SystemColors.WindowColor) : theme == ApplicationTheme.Dark;

        // High contrast brings its own system colors; WPF-UI maps them without an accent.
        if (!highContrast)
        {
            ApplicationAccentColorManager.ApplySystemAccent();
        }
    }

    public static bool IsLight(Color color) => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) > 128;
}
