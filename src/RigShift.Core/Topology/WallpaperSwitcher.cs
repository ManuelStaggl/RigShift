using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>Wallpaper per display, once a switch stays. Never fails the switch.</summary>
internal sealed class WallpaperSwitcher(IWallpapers wallpapers, ILogger log)
{
    private readonly IWallpapers _wallpapers = wallpapers;
    private readonly ILogger _log = log;

    /// <summary>Works on the displays the plan found: their device paths are the ones the shell knows now.</summary>
    public WallpaperOutcome Switch(TopologyPlan plan)
    {
        List<PlannedDisplay> wanted = [.. plan.Resolved.Where(d => !string.IsNullOrWhiteSpace(d.Assignment.Wallpaper))];
        if (wanted.Count == 0)
        {
            return WallpaperOutcome.NotConfigured;
        }

        WallpaperFit fit = plan.Profile.WallpaperFit;
        bool missing = false;
        bool failed = false;
        if (fit == WallpaperFit.Span)
        {
            // One image for all displays: the primary display's, else the first one with an image.
            wanted = [wanted.FirstOrDefault(d => d.Assignment.IsPrimary) ?? wanted[0]];
        }

        foreach (PlannedDisplay display in wanted)
        {
            string image = display.Assignment.Wallpaper!;
            if (!File.Exists(image))
            {
                _log.Warning("Wallpaper {Image} for {Display} not found, left unchanged", image, DisplayNames.Of(display.Assignment));
                missing = true;
                continue;
            }

            string? monitor = fit == WallpaperFit.Span ? null : display.Target.Identity.TargetDevicePath;
            failed |= !_wallpapers.Show(monitor, image, fit);
        }

        _log.Information("Wallpapers of {Profile} set ({Fit}), missing files: {Missing}, failed: {Failed}", plan.Profile.Name, fit, missing, failed);
        return missing ? WallpaperOutcome.FileMissing : failed ? WallpaperOutcome.Incomplete : WallpaperOutcome.Applied;
    }
}
