using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;

namespace RigShift.Windows.Shell;

/// <summary>
/// The wallpaper per display through the shell's <c>IDesktopWallpaper</c>. Its monitor IDs are the device interface
/// paths the display configuration reports as <c>monitorDevicePath</c>, so a profile's display finds its wallpaper slot
/// without a lookup of its own.
/// </summary>
public sealed class Wallpapers : IWallpapers
{
    // Setting a wallpaper takes well under a second; this is for an Explorer that hangs.
    private static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(10);

    private readonly ILogger _log;

    public Wallpapers(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log;
    }

    public bool Show(string? monitorDevicePath, string imagePath, WallpaperFit fit)
    {
        ArgumentNullException.ThrowIfNull(imagePath);
        (bool finished, string? monitor, Exception? failure) = StaCall.Run(
            () =>
            {
                var wallpaper = (IDesktopWallpaper)new DesktopWallpaper();

                // The shell's own spelling: the paths it reports differ in case from the display configuration's.
                string? id = null;
                if (monitorDevicePath is not null)
                {
                    id = Monitors(wallpaper).FirstOrDefault(m => string.Equals(m, monitorDevicePath, StringComparison.OrdinalIgnoreCase));
                    if (id is null)
                    {
                        return null;
                    }
                }

                wallpaper.SetPosition(Position(fit));
                wallpaper.SetWallpaper(id!, imagePath);
                return id ?? "all displays";
            },
            TimeLimit);

        if (!finished)
        {
            _log.Warning("Could not set the wallpaper: Explorer did not answer within {Limit}", TimeLimit);
        }
        else if (failure is not null)
        {
            _log.Warning(failure, "Could not set the wallpaper {Image}", imagePath);
        }
        else if (monitor is null)
        {
            _log.Warning("Could not set the wallpaper {Image}: the shell does not know display {Display}", imagePath, monitorDevicePath);
        }
        else
        {
            _log.Information("Wallpaper {Image} ({Fit}) set on {Display}", imagePath, fit, monitor);
        }

        return finished && failure is null && monitor is not null;
    }

    public IReadOnlyList<KeyValuePair<string, string>> Read()
    {
        (bool finished, List<KeyValuePair<string, string>>? list, Exception? failure) = StaCall.Run(
            () =>
            {
                var wallpaper = (IDesktopWallpaper)new DesktopWallpaper();
                return Monitors(wallpaper).Select(m =>
                {
                    wallpaper.GetWallpaper(m, out PWSTR image);
                    return new KeyValuePair<string, string>(m, Take(image));
                }).ToList();
            },
            TimeLimit);
        if (!finished || failure is not null)
        {
            _log.Warning(failure, "Could not read the wallpapers (finished: {Finished})", finished);
        }

        return list ?? [];
    }

    private static List<string> Monitors(IDesktopWallpaper wallpaper)
    {
        wallpaper.GetMonitorDevicePathCount(out uint count);
        var monitors = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
        {
            wallpaper.GetMonitorDevicePathAt(i, out PWSTR id);
            monitors.Add(Take(id));
        }

        return monitors;
    }

    // The shell allocates the string; the caller frees it.
    private static unsafe string Take(PWSTR text)
    {
        try
        {
            return text.ToString() ?? string.Empty;
        }
        finally
        {
            PInvoke.CoTaskMemFree(text.Value);
        }
    }

    private static DESKTOP_WALLPAPER_POSITION Position(WallpaperFit fit) => fit switch
    {
        WallpaperFit.Fit => DESKTOP_WALLPAPER_POSITION.DWPOS_FIT,
        WallpaperFit.Stretch => DESKTOP_WALLPAPER_POSITION.DWPOS_STRETCH,
        WallpaperFit.Center => DESKTOP_WALLPAPER_POSITION.DWPOS_CENTER,
        WallpaperFit.Span => DESKTOP_WALLPAPER_POSITION.DWPOS_SPAN,
        _ => DESKTOP_WALLPAPER_POSITION.DWPOS_FILL,
    };
}
