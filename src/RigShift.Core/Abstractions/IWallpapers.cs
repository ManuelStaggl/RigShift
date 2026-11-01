using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for the desktop wallpaper per display.</summary>
public interface IWallpapers
{
    /// <summary>
    /// Shows <paramref name="imagePath"/> on the display with this device path, or on every display when it is
    /// <c>null</c>, fitted as <paramref name="fit"/> – Windows keeps one fit for all displays. Never throws; <c>false</c>
    /// when the display is unknown to the shell or the call failed, which is logged.
    /// </summary>
    bool Show(string? monitorDevicePath, string imagePath, WallpaperFit fit);

    /// <summary>Every display the shell knows with its wallpaper; empty without a desktop.</summary>
    IReadOnlyList<KeyValuePair<string, string>> Read();
}
