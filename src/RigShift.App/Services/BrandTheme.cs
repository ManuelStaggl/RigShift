using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;

namespace RigShift.App.Services;

/// <summary>
/// Keeps the brand resources in step with the WPF-UI theme: the matching token dictionary (light, dark or system
/// colors for high contrast), the symbol variant for the background, and the brand blue as accent color.
/// </summary>
public static class BrandTheme
{
    /// <summary>Key that exists only in a theme token dictionary, never in Shared or Profiles.</summary>
    private const string ThemeKey = "RigShift.Brush.selected";

    private static readonly Color BrandBlue = Color.FromRgb(0x00, 0x78, 0xD4);
    private static readonly Dictionary<string, BitmapImage> Images = [];

    /// <summary>Whether text and symbols should be light, i.e. the background is dark.</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(ApplicationTheme theme)
    {
        ResourceDictionary resources = Application.Current.Resources;
        bool highContrast = theme == ApplicationTheme.HighContrast;
        IsDark = highContrast ? !IsLight(SystemColors.WindowColor) : theme == ApplicationTheme.Dark;

        ResourceDictionary tokens = highContrast
            ? HighContrastTokens()
            : new ResourceDictionary { Source = BrandUri(IsDark ? "RigShift.Dark.xaml" : "RigShift.Light.xaml") };

        System.Collections.ObjectModel.Collection<ResourceDictionary> merged = resources.MergedDictionaries;
        int index = merged.ToList().FindIndex(d => d.Contains(ThemeKey));
        if (index < 0)
        {
            merged.Add(tokens);
        }
        else
        {
            merged[index] = tokens;
        }

        resources["RigShift.Image.symbol"] = Image($"rigshift-symbol-color-{(IsDark ? "dark" : "light")}-128w.png");

        if (!highContrast)
        {
            ApplicationAccentColorManager.Apply(BrandBlue, theme);
        }
    }

    public static bool IsLight(Color color) => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) > 128;

    private static Uri BrandUri(string file) => new("pack://application:,,,/Assets/Brand/" + file, UriKind.Absolute);

    private static BitmapImage Image(string file)
    {
        if (!Images.TryGetValue(file, out BitmapImage? image))
        {
            image = new BitmapImage(BrandUri(file));
            image.Freeze();
            Images[file] = image;
        }

        return image;
    }

    /// <summary>High contrast: every brand brush maps to a system color, so the user's contrast theme wins.</summary>
    private static ResourceDictionary HighContrastTokens() => new()
    {
        ["RigShift.Brush.background"] = SystemColors.WindowBrush,
        ["RigShift.Brush.surface"] = SystemColors.WindowBrush,
        ["RigShift.Brush.surfaceAlt"] = SystemColors.ControlBrush,
        ["RigShift.Brush.text"] = SystemColors.WindowTextBrush,
        ["RigShift.Brush.textMuted"] = SystemColors.GrayTextBrush,
        ["RigShift.Brush.border"] = SystemColors.WindowTextBrush,
        ["RigShift.Brush.accent"] = SystemColors.HighlightBrush,
        ["RigShift.Brush.focus"] = SystemColors.HighlightBrush,
        // "primary" is used as text on "selected" (active profile row).
        ["RigShift.Brush.selected"] = SystemColors.HighlightBrush,
        ["RigShift.Brush.primary"] = SystemColors.HighlightTextBrush,
        ["RigShift.Brush.onPrimary"] = SystemColors.HighlightBrush,
        ["RigShift.Brush.success"] = SystemColors.WindowTextBrush,
        ["RigShift.Brush.warning"] = SystemColors.WindowTextBrush,
        ["RigShift.Brush.error"] = SystemColors.WindowTextBrush,
    };
}
