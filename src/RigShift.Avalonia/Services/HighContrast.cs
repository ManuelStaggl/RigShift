using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>
/// Windows high contrast: the app's surface, text, line and accent tokens take the user's system colours, set on the
/// application itself so they win over both theme dictionaries; leaving high contrast removes them again. Follows the
/// setting live. Semi's own control keys keep the palette, so controls drawn only by Semi stay in the theme's colours,
/// except the toggles. A selected row or rail tile keeps the window colour behind its text and gets a ring in the
/// highlight colour: text in the window text colour on the highlight fill would be unreadable in most contrast themes.
/// </summary>
internal static class HighContrast
{
    private static readonly (string Key, SystemColorKind Kind)[] Map =
    [
        ("RsRail", SystemColorKind.Window), ("RsContent", SystemColorKind.Window), ("RsColumn", SystemColorKind.Window),
        ("RsCard", SystemColorKind.Window), ("RsPop", SystemColorKind.Window), ("RsDeep", SystemColorKind.Window),
        ("RsBay", SystemColorKind.Window), ("RsTileFill", SystemColorKind.Window), ("RsKey", SystemColorKind.Window),
        ("RsRaised", SystemColorKind.ButtonFace), ("RsRaisedHover", SystemColorKind.ButtonFace), ("RsRaisedPressed", SystemColorKind.ButtonFace),
        ("RsHover", SystemColorKind.ButtonFace), ("RsAccentSoft", SystemColorKind.Window), ("RsAccentDuo", SystemColorKind.Window),
        ("RsWarningSoft", SystemColorKind.Window), ("RsDangerSoft", SystemColorKind.Window), ("RsRailOn", SystemColorKind.Window),
        ("RsText", SystemColorKind.WindowText), ("RsText2", SystemColorKind.WindowText), ("RsText3", SystemColorKind.WindowText),
        ("RsText4", SystemColorKind.GrayText), ("RsRailOnText", SystemColorKind.Highlight),
        ("RsDivider", SystemColorKind.WindowText), ("RsNoticeLine", SystemColorKind.WindowText), ("RsOutline", SystemColorKind.WindowText), ("RsTileStroke", SystemColorKind.WindowText),
        ("RsAccent", SystemColorKind.Highlight), ("RsAccentHover", SystemColorKind.Highlight), ("RsOnAccent", SystemColorKind.HighlightText),
        ("RsAccentText", SystemColorKind.HotLight), ("RsSuccess", SystemColorKind.HotLight), ("RsWarning", SystemColorKind.HotLight),
        ("RsDanger", SystemColorKind.HotLight),
        ("RsScreenSurface", SystemColorKind.Window), ("RsScreenEdge", SystemColorKind.WindowText),
        ("RsDiagramField", SystemColorKind.Window),
        // Toggles: on in the highlight, off in the grey text colour, the knob in the highlight text colour on both.
        ("ToggleSwitchContainerCheckedDefaultBackground", SystemColorKind.Highlight), ("ToggleSwitchContainerCheckedPointeroverBackground", SystemColorKind.Highlight),
        ("ToggleSwitchContainerCheckedPressedBackground", SystemColorKind.Highlight), ("ToggleSwitchContainerUnCheckedDefaultBackground", SystemColorKind.GrayText),
        ("ToggleSwitchContainerUnCheckedPointeroverBackground", SystemColorKind.GrayText), ("ToggleSwitchContainerUnCheckedPressedBackground", SystemColorKind.GrayText),
        ("ToggleSwitchIndicatorBackground", SystemColorKind.HighlightText),
    ];

    // The theme the app asked for before high contrast took over; null while it is off.
    private static ThemeVariant? _themeBefore;

    private static Color ColorOf(SystemColorKind kind)
    {
        uint rgb = NativeWindow.SystemColor(kind);
        return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    public static void Follow(Application app)
    {
        if (app.PlatformSettings is not { } settings)
        {
            return;
        }

        Apply(app, settings.GetColorValues());
        settings.ColorValuesChanged += (_, values) => Apply(app, values);
    }

    private static void Apply(Application app, PlatformColorValues values)
    {
        bool on = values.ContrastPreference == ColorContrastPreference.High;
#if DEBUG
        // Screenshots of the high-contrast mapping without changing Windows: RIGSHIFT_PREVIEW_CONTRAST=1.
        on |= Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_CONTRAST") == "1";
#endif
        foreach ((string key, SystemColorKind kind) in Map)
        {
            if (on)
            {
                app.Resources[key] = new SolidColorBrush(ColorOf(kind));
            }
            else
            {
                app.Resources.Remove(key);
            }
        }

        if (on)
        {
            app.Resources["RsRailOnShadow"] = new BoxShadows(new BoxShadow { IsInset = true, Spread = 2, Color = ColorOf(SystemColorKind.Highlight) });
        }
        else
        {
            app.Resources.Remove("RsRailOnShadow");
        }

        // Semi's controls keep their palette; the theme whose base matches the system background keeps their text readable.
        if (on)
        {
            _themeBefore ??= app.RequestedThemeVariant;
            Color window = ColorOf(SystemColorKind.Window);
            app.RequestedThemeVariant = (0.299 * window.R) + (0.587 * window.G) + (0.114 * window.B) < 128 ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        else if (_themeBefore is { } before)
        {
            app.RequestedThemeVariant = before;
            _themeBefore = null;
        }

        Log.Information("High contrast {State}", on ? "on: system colours" : "off");
    }
}
