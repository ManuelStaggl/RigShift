namespace RigShift.Core.Abstractions;

/// <summary>
/// The OpenXR runtimes Windows knows (SteamVR, Virtual Desktop, Meta). Read-only: RigShift never sets the system's
/// active runtime – a game runs with a chosen one through its own environment instead.
/// </summary>
public interface IOpenXrRuntimes
{
    /// <summary>Every registered runtime with an existing manifest; empty when there is none or the registry cannot be read.</summary>
    IReadOnlyList<OpenXrRuntime> Installed();
}

/// <param name="ManifestPath">Full path of the runtime's JSON manifest, the value for <c>XR_RUNTIME_JSON</c>.</param>
/// <param name="Name">The runtime's own name from the manifest, else the manifest's file name.</param>
/// <param name="IsActive">The runtime Windows uses when nothing else is said.</param>
public sealed record OpenXrRuntime(string ManifestPath, string Name, bool IsActive);
