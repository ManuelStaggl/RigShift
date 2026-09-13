namespace RigShift.Core.Profiles;

/// <summary>
/// The built-in profile symbols of the brand package. Profiles store the key in <see cref="Profile.Icon"/>;
/// the app draws the matching geometry (tray icon, profile list, editor).
/// </summary>
public static class ProfileIcons
{
    public const string Desk = "desk";
    public const string Rig = "rig";
    public const string Vr = "vr";
    public const string Tv = "tv";
    public const string Stream = "stream";

    public static IReadOnlyList<string> All { get; } = [Desk, Rig, Vr, Tv, Stream];

    /// <summary>The known key for <paramref name="key"/> (case-insensitive, blanks ignored), or <c>null</c> if there is none.</summary>
    public static string? Normalize(string? key) =>
        key is null ? null : All.FirstOrDefault(k => string.Equals(k, key.Trim(), StringComparison.OrdinalIgnoreCase));
}
