using System.Text.Json;
using RigShift.Core.Profiles;

namespace RigShift.Core.Legacy;

/// <summary>
/// Reader for <c>DisplayProfiles.json</c> of the original script:
/// <code>{ "Profiles": { "Rig": { "Audio": { "DeviceName": "...", "DeviceId": "{0.0.0.00000000}.{guid}" } } } }</code>
/// </summary>
public static class LegacyAudioConfig
{
    /// <summary>Playback device per profile name (case-insensitive). Profiles without a device ID are left out.</summary>
    public static IReadOnlyDictionary<string, AudioEndpoint> Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        var result = new Dictionary<string, AudioEndpoint>(StringComparer.OrdinalIgnoreCase);
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true });

        if (!document.RootElement.TryGetProperty("Profiles", out JsonElement profiles) || profiles.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        foreach (JsonProperty profile in profiles.EnumerateObject())
        {
            if (profile.Value.ValueKind == JsonValueKind.Object
                && profile.Value.TryGetProperty("Audio", out JsonElement audio)
                && audio.ValueKind == JsonValueKind.Object
                && audio.TryGetProperty("DeviceId", out JsonElement id)
                && id.GetString() is { Length: > 0 } deviceId)
            {
                string name = audio.TryGetProperty("DeviceName", out JsonElement deviceName) ? deviceName.GetString() ?? string.Empty : string.Empty;
                result[profile.Name] = new AudioEndpoint(deviceId, name);
            }
        }

        return result;
    }
}
