using System.Text;
using System.Text.Json;

namespace RigShift.Core.Games;

/// <summary>
/// Values a game variant keeps for itself in a settings file the game shares between variants – Le Mans Ultimate keeps
/// its graphics for the monitor and for VR in one <c>Settings.JSON</c>. Written before the game starts, taken back
/// from the file once it has ended (the user may have changed them in the game's menu), and the values from before
/// are put back.
/// </summary>
public sealed record GameSettings
{
    /// <summary>The game's JSON or INI settings file. Environment variables are expanded.</summary>
    public required string File { get; init; }

    /// <summary>The values to keep, as <c>Section/Name</c> paths through the JSON objects or INI sections.</summary>
    public IReadOnlyList<string> Keys { get; set; } = [];

    /// <summary>
    /// The variant's values as raw JSON or INI text (<c>25</c>, <c>0.10000000149011612</c>), by key. Raw so a number
    /// comes back exactly as the game wrote it. Empty until the first session ends: then the user has set them in the game.
    /// </summary>
    public IReadOnlyDictionary<string, string> Values { get; set; } = new Dictionary<string, string>();

    /// <summary>The file with its environment variables expanded.</summary>
    public string FullPath => Environment.ExpandEnvironmentVariables(File.Trim().Trim('"'));

    /// <summary>
    /// A full path to a <c>.json</c> or <c>.ini</c> file. <c>games.json</c> can come from somebody else, and nothing but
    /// a settings file of the game's is ever meant to be written here.
    /// </summary>
    public bool HasValidFile
    {
        get
        {
            string path = FullPath;
            return path.Length > 0 && Path.IsPathFullyQualified(path)
                && (IsIni(path) || string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>The file is read and written as INI (<see cref="GameSettingsIni"/>), otherwise as JSON.</summary>
    public static bool IsIni(string path) => string.Equals(Path.GetExtension(path), ".ini", StringComparison.OrdinalIgnoreCase);

    /// <summary>Separates the objects of a key path.</summary>
    public const char PathSeparator = '/';

    /// <summary>Every single value of a settings file with its key, in file order, for picking keys from a list.</summary>
    /// <exception cref="JsonException">A JSON file that is not valid JSON.</exception>
    /// <exception cref="InvalidDataException">A UTF-16 INI file.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> ListValues(string path, ReadOnlySpan<byte> content)
    {
        ArgumentNullException.ThrowIfNull(path);
        return IsIni(path) ? GameSettingsIni.All(content) : GameSettingsJson.All(content);
    }
}

/// <summary>
/// Reads and replaces single values in a JSON document without touching anything else: order, comments, blanks, line
/// ends and the way numbers are written stay byte for byte. A serializer round trip would rewrite all of that, and the
/// game would get a file that differs everywhere from what it wrote.
/// </summary>
public static class GameSettingsJson
{
    private static readonly JsonReaderOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The raw values of those <paramref name="keys"/> that are in the document and hold a single value.</summary>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    public static IReadOnlyDictionary<string, string> Read(ReadOnlySpan<byte> utf8, IEnumerable<string> keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var wanted = new HashSet<string>(keys, StringComparer.Ordinal);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string key, int start, int length) in Values(utf8))
        {
            if (wanted.Contains(key))
            {
                values[key] = Encoding.UTF8.GetString(utf8.Slice(start, length));
            }
        }

        return values;
    }

    /// <summary>Every single value with its key path, in document order; a repeated path counts once.</summary>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    public static IReadOnlyList<KeyValuePair<string, string>> All(ReadOnlySpan<byte> utf8)
    {
        var all = new List<KeyValuePair<string, string>>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string key, int start, int length) in Values(utf8))
        {
            if (seen.Add(key))
            {
                all.Add(new(key, Encoding.UTF8.GetString(utf8.Slice(start, length))));
            }
        }

        return all;
    }

    /// <summary>The document with the given values replaced; keys it does not hold are skipped.</summary>
    /// <exception cref="JsonException">The document is not valid JSON.</exception>
    /// <exception cref="ArgumentException">A value is not a single JSON value.</exception>
    public static byte[] Replace(ReadOnlySpan<byte> utf8, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        foreach ((string key, string value) in values)
        {
            if (!IsSingleValue(value))
            {
                throw new ArgumentException($"The value for {key} is not a single JSON value.", nameof(values));
            }
        }

        var output = new List<byte>(utf8.Length + 64);
        int copied = 0;
        foreach ((string key, int start, int length) in Values(utf8))
        {
            if (values.TryGetValue(key, out string? value))
            {
                output.AddRange(utf8[copied..start]);
                output.AddRange(Encoding.UTF8.GetBytes(value));
                copied = start + length;
            }
        }

        output.AddRange(utf8[copied..]);
        return [.. output];
    }

    /// <summary>
    /// A string, number, <c>true</c>, <c>false</c> or <c>null</c> and nothing else. The values come from
    /// <c>games.json</c>; an object or a second value would break the game's file.
    /// </summary>
    public static bool IsSingleValue(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        try
        {
            var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw));
            return reader.Read()
                && reader.TokenType is JsonTokenType.String or JsonTokenType.Number or JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null
                && !reader.Read();
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static List<(string Key, int Start, int Length)> Values(ReadOnlySpan<byte> utf8)
    {
        var found = new List<(string, int, int)>();
        var reader = new Utf8JsonReader(utf8, Options);

        // Names of the open containers from the root down; null for the root and for arrays.
        var open = new List<string?>();
        string? property = null;
        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    property = reader.GetString();
                    break;
                case JsonTokenType.StartObject:
                    open.Add(open.Count == 0 ? null : property);
                    property = null;
                    break;
                case JsonTokenType.StartArray:
                    open.Add(null);
                    property = null;
                    break;
                case JsonTokenType.EndObject or JsonTokenType.EndArray:
                    open.RemoveAt(open.Count - 1);
                    break;
                default:
                    if (property is not null && open.Skip(1).All(name => name is not null))
                    {
                        // A string's raw span leaves out its quotes; the replacement brings its own.
                        int start = (int)reader.TokenStartIndex;
                        int length = reader.TokenType == JsonTokenType.String ? reader.ValueSpan.Length + 2 : reader.ValueSpan.Length;
                        found.Add((string.Join(GameSettings.PathSeparator, open.Skip(1).Append(property)), start, length));
                    }

                    property = null;
                    break;
            }
        }

        return found;
    }
}
