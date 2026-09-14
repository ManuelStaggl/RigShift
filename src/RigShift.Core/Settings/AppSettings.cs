using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace RigShift.Core.Settings;

/// <summary>Application settings, stored as <c>%LocalAppData%\RigShift\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Profile shown first and optionally applied at startup.</summary>
    public Guid? DefaultProfileId { get; init; }

    public bool ApplyDefaultProfileOnStartup { get; init; }

    /// <summary>Confirmation timeout for profiles without their own value. 0 disables the safety net.</summary>
    public int ConfirmTimeoutSeconds { get; init; } = 15;

    /// <summary>UI language: <c>null</c> follows Windows, otherwise a culture name such as <c>en</c> or <c>de</c>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Only report newer versions instead of downloading them and installing on the next start. Phrased so that
    /// <c>false</c> is the default: the JSON source generator does not apply property initializers to missing keys, so
    /// files written by 1.0 must mean automatic installation without the key.
    /// </summary>
    public bool OnlyNotifyAboutUpdates { get; init; }
}

/// <summary>Loads and saves <see cref="AppSettings"/>. A missing or unreadable file yields defaults, never an exception.</summary>
public sealed class JsonSettingsStore
{
    private readonly string _file;
    private readonly ILogger _log;

    public JsonSettingsStore(string file, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(log);
        _file = Path.GetFullPath(file);
        _log = log.ForContext<JsonSettingsStore>();
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_file))
        {
            return new AppSettings();
        }

        try
        {
            await using FileStream stream = File.OpenRead(_file);
            AppSettings? settings = await JsonSerializer.DeserializeAsync(stream, SettingsJsonContext.Default.AppSettings, cancellationToken);
            return settings ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Settings file {File} could not be read, using defaults", _file);
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? ".");

        string temp = _file + ".tmp";
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream, settings with { SchemaVersion = AppSettings.CurrentSchemaVersion }, SettingsJsonContext.Default.AppSettings, cancellationToken);
        }

        File.Move(temp, _file, overwrite: true);
        _log.Information("Settings saved to {File}", _file);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
