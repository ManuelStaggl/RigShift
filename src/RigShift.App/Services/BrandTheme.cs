using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;

namespace RigShift.App.Services;

/// <summary>
/// RigShift has one theme, dark, from the brand tokens (Resources/Tokens.xaml). When Windows runs a high contrast
/// theme, the tokens are swapped for TokensHighContrast.xaml (same keys, system colors) and WPF-UI supplies its own
/// high contrast theme for the stock controls. Light is never applied.
/// </summary>
public static class BrandTheme
{
    private static readonly Uri DarkTokens = new("pack://application:,,,/Resources/Tokens.xaml");
    private static readonly Uri HighContrastTokens = new("pack://application:,,,/Resources/TokensHighContrast.xaml");

    /// <summary>Whether text and symbols are light, i.e. the background is dark (false only in a light high contrast theme).</summary>
    public static bool IsDark { get; private set; } = true;

    public static void Apply(ApplicationTheme theme)
    {
        bool highContrast = theme == ApplicationTheme.HighContrast;
        IsDark = !highContrast || !IsLight(SystemColors.WindowColor);

        // High contrast is always reloaded: its colors can change without the theme changing.
        var merged = Application.Current.Resources.MergedDictionaries;
        int index = merged.ToList().FindIndex(d => d.Source == DarkTokens || d.Source == HighContrastTokens);
        Uri wanted = highContrast ? HighContrastTokens : DarkTokens;
        if (index >= 0 && merged[index].Source == wanted && !highContrast)
        {
            return;
        }

        var tokens = new ResourceDictionary { Source = wanted };
        if (index >= 0)
        {
            merged[index] = tokens;
        }
        else
        {
            merged.Add(tokens);
        }
    }

    public static bool IsLight(Color color) => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B) > 128;
}
