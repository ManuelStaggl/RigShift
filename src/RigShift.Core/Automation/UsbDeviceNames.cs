namespace RigShift.Core.Automation;

/// <summary>
/// Custom USB device names (user decision U-01): given once and shown everywhere – in rules, when apps wait for a device
/// and in notifications. Stored in the settings by <c>VID_xxxx&amp;PID_xxxx</c>, like monitor names.
/// </summary>
public static class UsbDeviceNames
{
    public const int MaxCustomNameLength = 40;

    /// <summary>Trimmed and shortened to <see cref="MaxCustomNameLength"/>; <c>null</c> when empty.</summary>
    public static string? Normalize(string? customName)
    {
        string? trimmed = customName?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, MaxCustomNameLength)].TrimEnd();
    }

    /// <summary>The custom name of a device, or <c>null</c>.</summary>
    public static string? CustomNameOf(string? deviceId, IReadOnlyDictionary<string, string>? registry) =>
        UsbDeviceIds.Normalize(deviceId) is { } id && registry is not null
            ? registry.FirstOrDefault(p => string.Equals(p.Key, id, StringComparison.OrdinalIgnoreCase)).Value is { } name ? Normalize(name) : null
            : null;

    /// <summary>Short name for messages: the custom name, else Windows' name, else the id.</summary>
    public static string NameOf(string? deviceId, string? windowsName, IReadOnlyDictionary<string, string>? registry) =>
        CustomNameOf(deviceId, registry)
        ?? (string.IsNullOrWhiteSpace(windowsName) ? UsbDeviceIds.Normalize(deviceId) ?? deviceId ?? "?" : windowsName);

    /// <summary>For device lists: "Wheel · CSL DD" with a custom name, Windows' name alone without.</summary>
    public static string Label(string? deviceId, string? windowsName, IReadOnlyDictionary<string, string>? registry)
    {
        string windows = string.IsNullOrWhiteSpace(windowsName) ? UsbDeviceIds.Normalize(deviceId) ?? deviceId ?? "?" : windowsName;
        return CustomNameOf(deviceId, registry) is { } custom && !string.Equals(custom, windows, StringComparison.OrdinalIgnoreCase)
            ? $"{custom} · {windows}"
            : windows;
    }

    /// <summary>"Wheel + Pedals" for logs and messages; <c>null</c> for a rule without devices.</summary>
    public static string? Describe(AutomationRule rule, IReadOnlyDictionary<string, string>? registry)
    {
        ArgumentNullException.ThrowIfNull(rule);
        List<string> names = (rule.Migrated().Devices ?? [])
            .Where(d => UsbDeviceIds.Normalize(d.Id) is not null)
            .Select(d => NameOf(d.Id, d.Name, registry))
            .ToList();
        return names.Count == 0 ? null : string.Join(" + ", names);
    }

    /// <summary>The registry with <paramref name="name"/> set for <paramref name="deviceId"/>, or removed when blank.</summary>
    public static IReadOnlyDictionary<string, string> WithName(IReadOnlyDictionary<string, string>? registry, string deviceId, string? name)
    {
        string id = UsbDeviceIds.Normalize(deviceId) ?? throw new ArgumentException("Not a USB device id: " + deviceId, nameof(deviceId));
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, string value) in registry ?? new Dictionary<string, string>())
        {
            if (UsbDeviceIds.Normalize(key) is { } normalizedKey && Normalize(value) is { } normalizedValue)
            {
                names[normalizedKey] = normalizedValue;
            }
        }

        if (Normalize(name) is { } normalized)
        {
            names[id] = normalized;
        }
        else
        {
            names.Remove(id);
        }

        return names;
    }
}
