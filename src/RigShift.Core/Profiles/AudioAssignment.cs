namespace RigShift.Core.Profiles;

/// <summary>
/// Default audio endpoints for a profile. Each entry is optional: <c>null</c> leaves that role untouched.
/// Endpoint IDs look like <c>{0.0.0.00000000}.{guid}</c> and are stable per device on a given machine.
/// </summary>
public sealed record AudioAssignment
{
    /// <summary>Default playback device (console + multimedia roles).</summary>
    public AudioEndpoint? Playback { get; init; }

    /// <summary>Playback device for the communications role (Discord, Crew Chief). Falls back to <see cref="Playback"/>.</summary>
    public AudioEndpoint? PlaybackCommunications { get; init; }

    /// <summary>Default recording device (v1.1).</summary>
    public AudioEndpoint? Recording { get; init; }

    public AudioEndpoint? RecordingCommunications { get; init; }

    /// <summary>Target volume in percent for the playback device. <c>null</c> leaves volume unchanged.</summary>
    public int? PlaybackVolumePercent { get; init; }

    /// <summary>Target level in percent for the recording device. <c>null</c> leaves it unchanged.</summary>
    public int? RecordingVolumePercent { get; init; }
}

public sealed record AudioEndpoint(string EndpointId, string FriendlyName);
