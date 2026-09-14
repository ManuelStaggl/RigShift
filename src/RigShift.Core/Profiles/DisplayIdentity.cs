namespace RigShift.Core.Profiles;

/// <summary>
/// Stable identity of a physical (or virtual) display across reboots and reconnects.
/// Adapter LUIDs and target IDs are volatile and deliberately NOT part of this record;
/// they are resolved at switch time by matching these fields against the live topology.
/// </summary>
public sealed record DisplayIdentity
{
    /// <summary>Device interface path of the GPU (or virtual adapter such as spacedesk), e.g. <c>\\?\PCI#VEN_10DE&amp;DEV_2702...</c>.</summary>
    public required string AdapterDevicePath { get; init; }

    /// <summary>Monitor device interface path, e.g. <c>\\?\DISPLAY#SAM749B#...</c>. Primary matching key.</summary>
    public required string TargetDevicePath { get; init; }

    /// <summary>EDID manufacturer ID, secondary matching key when the device path changed (e.g. new cable/port).</summary>
    public ushort EdidManufacturerId { get; init; }

    public ushort EdidProductCodeId { get; init; }

    /// <summary>Human-readable name from EDID, for UI and logs only – never used for matching. <c>set</c>: an <c>init</c> initializer is skipped when the key is missing (docs/PLAN.md, stumbling blocks).</summary>
    public string FriendlyName { get; set; } = string.Empty;
}
