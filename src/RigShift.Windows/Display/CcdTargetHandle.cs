using System.Globalization;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Display;

/// <summary>Adapter LUID as a value type with equality. Volatile: valid for one snapshot only.</summary>
internal readonly record struct AdapterLuid(uint LowPart, int HighPart)
{
    public static AdapterLuid From(LUID luid) => new(luid.LowPart, luid.HighPart);

    public LUID ToLuid() => new() { LowPart = LowPart, HighPart = HighPart };

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{HighPart:X8}{LowPart:X8}");
}

/// <summary>A source (GDI view) a target can be driven from.</summary>
internal readonly record struct CcdSource(AdapterLuid Adapter, uint Id);

/// <summary>
/// <see cref="Core.Topology.AttachedDisplay.NativeHandle"/> for the CCD implementation: where the target lives right now
/// and which sources could drive it. Never persisted (display-topology.md, rule 2).
/// </summary>
internal sealed record CcdTargetHandle(AdapterLuid Adapter, uint TargetId, IReadOnlyList<CcdSource> Sources, CcdSource? ActiveSource)
{
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Adapter}:{TargetId}");
}
