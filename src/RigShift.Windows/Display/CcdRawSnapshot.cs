using System.Text.Json.Serialization;

namespace RigShift.Windows.Display;

/// <summary>
/// Everything a display snapshot is built from, as plain values: the CCD path and mode arrays plus the device names asked
/// for per target and adapter. Serializable, so a real machine's input can be dumped (<c>Probe snapshot --raw</c>) and,
/// anonymised, replayed in tests without hardware (analysis finding L-04). Adapters are LUIDs as 16 hex digits.
/// </summary>
public sealed record CcdRawSnapshot
{
    public required IReadOnlyList<CcdRawPath> Paths { get; init; }

    /// <summary>Indexed by <see cref="CcdRawPath.SourceModeIndex"/> and <see cref="CcdRawPath.TargetModeIndex"/>.</summary>
    public required IReadOnlyList<CcdRawMode> Modes { get; init; }

    /// <summary>One entry per target that appears in <see cref="Paths"/>.</summary>
    public required IReadOnlyList<CcdRawTarget> Targets { get; init; }

    /// <summary>One entry per adapter of a target with a monitor.</summary>
    public required IReadOnlyList<CcdRawAdapter> Adapters { get; init; }
}

public sealed record CcdRawPath(
    string SourceAdapter,
    uint SourceId,
    uint SourceModeIndex,
    string TargetAdapter,
    uint TargetId,
    uint TargetModeIndex,
    bool TargetAvailable,
    bool Active,
    uint RefreshNumerator,
    uint RefreshDenominator,
    int Rotation);

[JsonConverter(typeof(JsonStringEnumConverter<CcdModeKind>))]
public enum CcdModeKind
{
    Other,
    Source,
    Target,
}

/// <summary>Source modes use size and position, target modes the vertical sync frequency.</summary>
public sealed record CcdRawMode(CcdModeKind Kind, uint Width, uint Height, int PositionX, int PositionY, uint VSyncNumerator, uint VSyncDenominator);

/// <summary>The target name request; <paramref name="Hdr"/> is only asked for targets with an active path.</summary>
public sealed record CcdRawTarget(
    string Adapter,
    uint TargetId,
    int NameError,
    string MonitorDevicePath,
    string FriendlyName,
    bool EdidIdsValid,
    ushort EdidManufacturerId,
    ushort EdidProductCodeId,
    bool? Hdr);

public sealed record CcdRawAdapter(string Adapter, int Error, string DevicePath);
