namespace RigShift.Core.Topology;

/// <summary>Tuning for <see cref="TopologyPlanner"/>. Defaults match current NVIDIA GeForce cards.</summary>
public sealed record TopologyPlannerOptions
{
    /// <summary>Display heads per adapter when no override exists (NVIDIA GeForce: 4).</summary>
    public int DefaultHeadBudget { get; init; } = 4;

    /// <summary>Per-adapter head budget, keyed by adapter device path; wins over the vendor preset.</summary>
    public IReadOnlyDictionary<string, int> HeadBudgetByAdapter { get; init; } =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The head budget to check an adapter against, or <c>null</c> for no check. The two-heads-per-DSC-display rule is
    /// documented for NVIDIA only (docs/display-topology.md); AMD and Intel cards are not checked until community tests
    /// show their limits, so the warning never appears for a layout that works there. Adapters without a known vendor
    /// keep the default, as before.
    /// </summary>
    public int? HeadBudgetFor(string adapterDevicePath) =>
        HeadBudgetByAdapter.TryGetValue(adapterDevicePath, out int configured) ? configured
        : GpuVendors.Of(adapterDevicePath) is GpuVendor.Amd or GpuVendor.Intel ? null
        : DefaultHeadBudget;

    /// <summary>
    /// Pixel rate (width × height × refresh, in pixels per second) above which a display is assumed to need
    /// Display Stream Compression and therefore two heads. 4K@165 ≈ 1.37 Gpx/s, 5120×1440@240 ≈ 1.77 Gpx/s.
    /// </summary>
    public double DualHeadPixelRateThreshold { get; init; } = 1_000_000_000d;
}
