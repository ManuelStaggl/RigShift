namespace RigShift.Core.Profiles;

/// <summary>How a wallpaper fills its display, as in Windows' personalisation settings.</summary>
public enum WallpaperFit
{
    Fill,
    Fit,
    Stretch,
    Center,

    /// <summary>One image across all displays: the primary display's.</summary>
    Span,
}

/// <summary>What became of a profile's wallpapers.</summary>
public enum WallpaperOutcome
{
    /// <summary>No display in the profile has a wallpaper, or the switch did not get that far.</summary>
    NotConfigured,

    Applied,

    /// <summary>At least one image file is gone; the other displays got theirs.</summary>
    FileMissing,

    /// <summary>Windows did not take at least one wallpaper; see the log.</summary>
    Incomplete,
}
