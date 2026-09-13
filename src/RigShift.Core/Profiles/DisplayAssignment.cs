namespace RigShift.Core.Profiles;

/// <summary>One display inside a profile: identity plus desired mode and position.</summary>
public sealed record DisplayAssignment
{
    public required DisplayIdentity Identity { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Refresh rate as a rational (e.g. 240000/1000) to round-trip exactly what the driver reports.</summary>
    public required uint RefreshNumerator { get; init; }

    public required uint RefreshDenominator { get; init; }

    /// <summary>Desktop position of the top-left corner. The primary display is at (0,0).</summary>
    public required int PositionX { get; init; }

    public required int PositionY { get; init; }

    public DisplayRotation Rotation { get; init; } = DisplayRotation.Identity;

    public bool IsPrimary { get; init; }

    /// <summary>
    /// Optional displays (e.g. a spacedesk tablet) are skipped when absent and picked up later
    /// instead of failing the whole switch.
    /// </summary>
    public bool IsOptional { get; init; }
}

public enum DisplayRotation
{
    Identity = 1,
    Rotate90 = 2,
    Rotate180 = 3,
    Rotate270 = 4,
}
