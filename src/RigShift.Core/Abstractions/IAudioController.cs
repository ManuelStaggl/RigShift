using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for audio endpoints (Core Audio + undocumented IPolicyConfig on Windows).</summary>
public interface IAudioController
{
    Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(AudioDirection direction, CancellationToken cancellationToken);

    /// <summary>Sets the default endpoint for the given roles. Skips silently (with a log entry) if the device is not active.</summary>
    Task<bool> SetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken);

    Task SetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken);

    /// <summary>Current volume in percent, 0–100.</summary>
    Task<int> GetVolumeAsync(AudioEndpoint endpoint, CancellationToken cancellationToken);
}

public sealed record AudioDeviceInfo(AudioEndpoint Endpoint, AudioDirection Direction, bool IsActive, bool IsDefault);

public enum AudioDirection
{
    Render,
    Capture,
}

[Flags]
public enum AudioRoleMask
{
    None = 0,
    Console = 1,
    Multimedia = 2,
    Communications = 4,
    All = Console | Multimedia | Communications,
}
