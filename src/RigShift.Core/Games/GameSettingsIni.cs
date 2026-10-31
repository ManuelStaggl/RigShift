using System.Text;

namespace RigShift.Core.Games;

/// <summary>
/// <see cref="GameSettingsJson"/> for INI files: values addressed as <c>Section/Key</c> (a key before the first
/// section is just <c>Key</c>), replaced in place so comments, order, blanks and line ends stay byte for byte. Like
/// Windows' own INI functions, the first section and key of a name win; later duplicates are left alone. Section and
/// key names are matched case-insensitively. The value is everything after <c>=</c> up to the line end, without the
/// blanks around it; a <c>;</c> there belongs to the value, since many games write paths and lists that way.
/// </summary>
public static class GameSettingsIni
{
    /// <summary>The values of those <paramref name="keys"/> that are in the file, by the key as asked.</summary>
    /// <exception cref="InvalidDataException">The file is UTF-16, which is not supported.</exception>
    public static IReadOnlyDictionary<string, string> Read(ReadOnlySpan<byte> bytes, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        Dictionary<string, (int Start, int Length)> found = Values(bytes);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in keys)
        {
            if (found.TryGetValue(key, out (int Start, int Length) value))
            {
                values[key] = Encoding.UTF8.GetString(bytes.Slice(value.Start, value.Length));
            }
        }

        return values;
    }

    /// <summary>Every value with its <c>Section/Key</c>, in file order; a repeated key counts once, as it is read.</summary>
    /// <exception cref="InvalidDataException">The file is UTF-16, which is not supported.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> All(ReadOnlySpan<byte> bytes)
    {
        var all = new List<KeyValuePair<string, string>>();
        foreach ((string key, (int start, int length)) in Values(bytes).OrderBy(v => v.Value.Start))
        {
            all.Add(new(key, Encoding.UTF8.GetString(bytes.Slice(start, length))));
        }

        return all;
    }

    /// <summary>The file with the given values replaced; keys it does not hold are skipped.</summary>
    /// <exception cref="InvalidDataException">The file is UTF-16, which is not supported.</exception>
    /// <exception cref="ArgumentException">A value spans more than one line.</exception>
    public static byte[] Replace(ReadOnlySpan<byte> bytes, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach ((string key, string value) in values)
        {
            if (!IsSingleValue(value))
            {
                throw new ArgumentException($"The value for {key} is not a single line.", nameof(values));
            }
        }

        Dictionary<string, (int Start, int Length)> found = Values(bytes);
        var output = new List<byte>(bytes.Length + 64);
        int copied = 0;
        foreach ((int start, int length, string value) in values
            .Where(v => found.ContainsKey(v.Key))
            .Select(v => (found[v.Key].Start, found[v.Key].Length, v.Value))
            .OrderBy(v => v.Start))
        {
            if (start < copied)
            {
                // The same key twice in another spelling.
                continue;
            }

            output.AddRange(bytes[copied..start]);
            output.AddRange(Encoding.UTF8.GetBytes(value.Trim()));
            copied = start + length;
        }

        output.AddRange(bytes[copied..]);
        return [.. output];
    }

    /// <summary>One line of text: anything else would add lines to the game's file.</summary>
    public static bool IsSingleValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return !value.Contains('\r', StringComparison.Ordinal) && !value.Contains('\n', StringComparison.Ordinal);
    }

    /// <summary>Where the value of each <c>Section/Key</c> is, first occurrence only.</summary>
    private static Dictionary<string, (int Start, int Length)> Values(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            throw new InvalidDataException("UTF-16 INI files are not supported.");
        }

        var found = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        string? section = null;
        int lineStart = bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (lineStart < bytes.Length)
        {
            int newline = bytes[lineStart..].IndexOfAny((byte)'\r', (byte)'\n');
            int lineEnd = newline < 0 ? bytes.Length : lineStart + newline;
            (int start, int length) = Trimmed(bytes, lineStart, lineEnd);
            ReadOnlySpan<byte> line = bytes.Slice(start, length);
            if (line.Length > 0 && line[0] == '[' && line.IndexOf((byte)']') is var close and > 0)
            {
                section = Encoding.UTF8.GetString(line[1..close]).Trim();
            }
            else if (line.Length > 0 && line[0] is not (byte)';' and not (byte)'#' && line.IndexOf((byte)'=') is var equals and > 0)
            {
                string key = Encoding.UTF8.GetString(line[..equals]).Trim();
                (int valueStart, int valueLength) = Trimmed(bytes, start + equals + 1, start + length);
                found.TryAdd(section is null ? key : section + GameSettings.PathSeparator + key, (valueStart, valueLength));
            }

            // Past one line end: CRLF, LF or a lone CR.
            lineStart = lineEnd;
            if (lineStart < bytes.Length && bytes[lineStart] == '\r')
            {
                lineStart++;
            }

            if (lineStart < bytes.Length && bytes[lineStart] == '\n')
            {
                lineStart++;
            }
        }

        return found;
    }

    private static (int Start, int Length) Trimmed(ReadOnlySpan<byte> bytes, int start, int end)
    {
        while (start < end && bytes[start] is (byte)' ' or (byte)'\t')
        {
            start++;
        }

        while (end > start && bytes[end - 1] is (byte)' ' or (byte)'\t')
        {
            end--;
        }

        return (start, end - start);
    }
}
