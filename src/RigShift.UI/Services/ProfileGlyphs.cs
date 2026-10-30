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
}
