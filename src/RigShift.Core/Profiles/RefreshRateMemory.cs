using System.Globalization;

namespace RigShift.Core.Profiles;

/// <summary>
/// Refresh rates a display offered once, per target device path and resolution. Windows lists rates only for active
/// displays, so the profile editor could offer nothing else for a display that is off right now (finding HW-13).
/// Stored as <c>"numerator/denominator"</c> strings in the settings.
/// </summary>
public static class RefreshRateMemory
{
    public static string Key(DisplayIdentity identity, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return string.Create(CultureInfo.InvariantCulture, $"{identity.TargetDevicePath}|{width}x{height}");
    }

    public static IReadOnlyList<RefreshRate> Get(IReadOnlyDictionary<string, IReadOnlyList<string>>? memory, DisplayIdentity identity, int width, int height)
    {
        if (memory is null || !memory.TryGetValue(Key(identity, width, height), out IReadOnlyList<string>? stored))
        {
            return [];
        }

        var rates = new List<RefreshRate>(stored.Count);
        foreach (string text in stored)
        {
            string[] parts = text.Split('/');
            if (parts.Length == 2
                && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out uint numerator)
                && uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out uint denominator)
                && denominator != 0)
            {
                rates.Add(new RefreshRate(numerator, denominator));
            }
        }

        return rates;
    }

    /// <summary>The memory with these rates; the same instance when nothing changes, so callers can skip saving.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>>? With(
        IReadOnlyDictionary<string, IReadOnlyList<string>>? memory, DisplayIdentity identity, int width, int height, IReadOnlyList<RefreshRate> rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        if (rates.Count == 0)
        {
            return memory;
        }

        string key = Key(identity, width, height);
        List<string> texts = [.. rates.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Numerator}/{r.Denominator}"))];
        if (memory is not null && memory.TryGetValue(key, out IReadOnlyList<string>? stored) && stored.SequenceEqual(texts))
        {
            return memory;
        }

        var updated = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach ((string existingKey, IReadOnlyList<string> value) in memory ?? new Dictionary<string, IReadOnlyList<string>>())
        {
            updated[existingKey] = value;
        }

        updated[key] = texts;
        return updated;
    }
}
