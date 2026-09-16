using RigShift.App.Localization;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.Services;

/// <summary>Builds the topology picture's input from a profile's displays.</summary>
public static class TopologyDisplays
{
    /// <param name="missing">Device paths the last plan could not find; they are drawn as missing.</param>
    public static IReadOnlyList<TopologyDisplay> From(IEnumerable<DisplayAssignment> displays, IReadOnlySet<string>? missing = null)
    {
        ArgumentNullException.ThrowIfNull(displays);
        return displays.Select(d => From(d, missing?.Contains(d.Identity.TargetDevicePath) == true)).ToList();
    }

    public static TopologyDisplay From(DisplayAssignment display, bool isMissing = false)
    {
        ArgumentNullException.ThrowIfNull(display);
        string mode = Loc.Format("Displays_Mode", display.Width, display.Height, RefreshRate.Of(display).Hertz.ToString("0.##", Loc.Instance.Culture));
        return new TopologyDisplay
        {
            Key = display.Identity.TargetDevicePath,
            X = display.PositionX,
            Y = display.PositionY,
            Width = display.Width,
            Height = display.Height,
            Name = SwitchMessages.NameOf(display),
            Mode = mode,
            Details = DisplayNames.Of(display.Identity) + " · " + mode,
            State = isMissing ? TopologyDisplayState.Missing : TopologyDisplayState.Active,
            IsPrimary = display.IsPrimary,
            IsOptional = display.IsOptional,
        };
    }
}
