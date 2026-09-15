using System.Text.RegularExpressions;

namespace RigShift.Core.Topology;

public enum GpuVendor
{
    /// <summary>No PCI vendor id in the path, e.g. a virtual adapter (spacedesk, remote desktop), or an unlisted vendor.</summary>
    Unknown,
    Nvidia,
    Amd,
    Intel,
}

public static partial class GpuVendors
{
    /// <summary>The vendor from the PCI vendor id in an adapter device path (<c>\\?\PCI#VEN_10DE&amp;DEV_2702…</c>).</summary>
    public static GpuVendor Of(string? adapterDevicePath)
    {
        Match match = adapterDevicePath is null ? Match.Empty : VendorId().Match(adapterDevicePath);
        return !match.Success ? GpuVendor.Unknown : match.Groups[1].Value.ToUpperInvariant() switch
        {
            "10DE" => GpuVendor.Nvidia,
            "1002" or "1022" => GpuVendor.Amd,
            "8086" => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
    }

    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VendorId();
}
