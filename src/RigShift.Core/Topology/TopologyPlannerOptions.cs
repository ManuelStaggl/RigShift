namespace RigShift.Core.Topology;

/// <summary>Tuning for <see cref="TopologyPlanner"/>. Defaults match current NVIDIA GeForce cards.</summary>
public sealed record TopologyPlannerOptions
{
    /// <summary>Display heads per adapter when no override exists (NVIDIA GeForce: 4).</summary>
    public int DefaultHeadBudget { get; init; } = 4;

    /// <summary>Per-adapter head budget, keyed by adapter device path. AMD/Intel limits differ.</summary>
    public IReadOnlyDictionary<string, int> HeadBudgetByAdapter { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Pixel rate (width × height × refresh, in pixels per second) above which a display is assumed to need
    /// Display Stream Compression and therefore two heads. 4K@165 ≈ 1.37 Gpx/s, 5120×1440@240 ≈ 1.77 Gpx/s.
    /// </summary>
    public double DualHeadPixelRateThreshold { get; init; } = 1_000_000_000d;
}
