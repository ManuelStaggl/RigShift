using RigShift.Core.Profiles;

namespace RigShift.UI.Services;

/// <summary>The built-in profile and game symbols as Segoe Fluent Icons glyphs, plus the bindings that pick one.</summary>
public static class ProfileGlyphs
{
    public const string Game = "";

    /// <summary>The glyph for a symbol key, or <paramref name="fallback"/> when the key is unknown or empty.</summary>
    public static string For(string? key, string fallback) => ProfileIcons.Normalize(key) switch
    {
        ProfileIcons.Desk => "",
        ProfileIcons.Rig => "",
        ProfileIcons.Vr => "",
        ProfileIcons.Tv => "",
        ProfileIcons.Stream => "",
        _ => fallback,
    };

    /// <summary>For <c>x:Bind</c>: a game's symbol, or the generic game glyph.</summary>
    public static string ForGame(string? key) => For(key, Game);

    /// <summary>
    /// For <c>x:Bind</c> on <c>Path.Data</c>: the brand package's stroke drawing of a profile symbol, on a 24 × 24 grid;
    /// a profile without one gets the desk monitor. A new geometry each call: WinUI lets a geometry serve one path only.
    /// </summary>
    public static Microsoft.UI.Xaml.Media.Geometry Drawing(string? key) =>
        (Microsoft.UI.Xaml.Media.Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(
            typeof(Microsoft.UI.Xaml.Media.Geometry),
            ProfileIcons.Normalize(key) switch
            {
                ProfileIcons.Rig => "M4 20 H21 M7 20 L10 14 H16 L19 20 M5 4 V11 Q5 14 8 14 H13 M5 5 L3 3 M16 6 L19 9 M17.5 7.5 L13 12 M14 5 Q18 3 21 8",
                ProfileIcons.Vr => "M5 7 H19 Q22 7 22 10 V15 Q22 17 19 17 H16 L13 14 H11 L8 17 H5 Q2 17 2 15 V10 Q2 7 5 7 Z M2 11 H1 M22 11 H23",
                ProfileIcons.Tv => "M4 2 H20 V11 H4 Z M10 11 V14 M7 17 V15 H17 V17 M4 16 V21 H20 V16 M4 19 H20 M7 21 V23 M17 21 V23",
                ProfileIcons.Stream => "M12 3 Q9 3 9 6 V11 Q9 14 12 14 Q15 14 15 11 V6 Q15 3 12 3 Z M6 10 V11 Q6 17 12 17 Q18 17 18 11 V10 M12 17 V21 M8 21 H16 M3 4 V8 M21 4 V8",
                _ => "M3 4 H21 V15 H3 Z M12 15 V19 M8 19 H16 M2 21 V18 M22 21 V18 M2 18 H22",
            });
}
