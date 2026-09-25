using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for audio endpoints (Core Audio + undocumented IPolicyConfig on Windows). The native work runs off the
/// calling thread, so every call can be awaited from the UI thread.
/// </summary>
public interface IAudioController
{
    Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(AudioDirection direction, CancellationToken cancellationToken);

    /// <summary>Sets the default endpoint for the given roles. Skips silently (with a log entry) if the device is not active.</summary>
    Task<bool> SetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken);

    Task SetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken);

    /// <summary>Current volume in percent, 0–100.</summary>
    Task<int> GetVolumeAsync(AudioEndpoint endpoint, CancellationToken cancellationToken);
}

/// <param name="DefaultRoles">The roles this device is the default for; Windows keeps one default per role.</param>
public sealed record AudioDeviceInfo(AudioEndpoint Endpoint, AudioDirection Direction, bool IsActive, AudioRoleMask DefaultRoles)
{
    /// <summary>The device Windows shows as the default – the one for the console role.</summary>
    public bool IsDefault => DefaultRoles.HasFlag(AudioRoleMask.Console);
}

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
