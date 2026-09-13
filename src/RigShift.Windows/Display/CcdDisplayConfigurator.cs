using RigShift.Core.Abstractions;
using RigShift.Core.Topology;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Display;

/// <summary>
/// <see cref="IDisplayConfigurator"/> on top of the Windows CCD API.
/// Reference implementation of the algorithm: <c>legacy/DisplayProfile.ps1</c> (proven on real hardware).
/// Rules that must survive any refactoring are documented in <c>docs/display-topology.md</c>.
/// </summary>
public sealed class CcdDisplayConfigurator : IDisplayConfigurator
{
    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        // Sanity check that CsWin32 generated the CCD entry points; the real implementation follows in v1.
        WIN32_ERROR error = PInvoke.GetDisplayConfigBufferSizes(QUERY_DISPLAY_CONFIG_FLAGS.QDC_ALL_PATHS, out uint _, out uint _);
        return error == WIN32_ERROR.ERROR_SUCCESS
            ? throw new NotImplementedException("Planned for v1 – see docs/PLAN.md, Meilenstein M1.")
            : throw new InvalidOperationException($"GetDisplayConfigBufferSizes failed with {error}.");
    }

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken)
        => throw new NotImplementedException("Planned for v1 – see docs/PLAN.md, Meilenstein M1.");
}
