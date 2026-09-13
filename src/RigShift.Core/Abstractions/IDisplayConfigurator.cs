using RigShift.Core.Topology;

namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for display topology. The Windows implementation wraps the CCD API
/// (QueryDisplayConfig / SetDisplayConfig); tests use an in-memory fake.
/// </summary>
public interface IDisplayConfigurator
{
    /// <summary>Queries all paths (active and inactive) so that sleeping or optional displays are visible to the planner.</summary>
    Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Applies the plan in ONE atomic SetDisplayConfig call. Never enables or disables displays one by one –
    /// that is exactly what fails against NVIDIA's display-head limit.
    /// </summary>
    /// <returns>0 on success, otherwise the native error code.</returns>
    Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken);
}

public sealed record ApplyOptions
{
    /// <summary>When true, saved modes are dropped and the OS chooses modes from its database (fallback path).</summary>
    public bool UseDatabaseModes { get; init; }

    /// <summary>Persist the resulting topology to the OS display database (SDC_SAVE_TO_DATABASE).</summary>
    public bool SaveToDatabase { get; init; } = true;
}
