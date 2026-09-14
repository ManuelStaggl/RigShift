using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Automation;
using Serilog;

namespace RigShift.Core.Settings;

/// <summary>Application settings, stored as <c>%AppData%\RigShift\settings.json</c>.</summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>Profile shown first and optionally applied at startup.</summary>
    public Guid? DefaultProfileId { get; init; }

    public bool ApplyDefaultProfileOnStartup { get; init; }

    /// <summary>
    /// Confirmation timeout for profiles without their own value. 0 disables the safety net. A regular setter on
    /// purpose: the JSON source generator sets init-only properties through an object initializer and turns a missing
    /// key into 0, which would silently switch the safety net off. With <c>set</c> it keeps the initializer value.
    /// </summary>
    public int ConfirmTimeoutSeconds { get; set; } = 15;

    /// <summary>UI language: <c>null</c> follows Windows, otherwise a culture name such as <c>en</c> or <c>de</c>.</summary>
    public string? Language { get; init; }

    /// <summary>
    /// Only report newer versions instead of downloading them and installing on the next start. Phrased so that
    /// <c>false</c> is the default: the JSON source generator does not apply property initializers to missing keys, so
    /// files written by 1.0 must mean automatic installation without the key.
    /// </summary>
    public bool OnlyNotifyAboutUpdates { get; init; }

    /// <summary>
    /// Custom monitor names by target device path, also for monitors that are in no profile (displays page). Profiles
    /// carry the name too, so the core and the command line do not need the settings (docs/PLAN.md, section 6).
    /// </summary>
    public IReadOnlyDictionary<string, string>? DisplayNames { get; init; }

    /// <summary>USB device rules of the automation page (docs/PLAN.md, section 6).</summary>
    public IReadOnlyList<AutomationRule>? AutomationRules { get; init; }

    /// <summary>All rules paused, e.g. from the tray menu. <c>false</c> is the default for files without the key.</summary>
    public bool AutomationPaused { get; init; }

    /// <summary>
    /// A communications ducking value is remembered in <see cref="DuckingBeforeProfiles"/> (analysis finding B-01). Separate
    /// flag because the remembered value itself may be <c>null</c> (registry value missing). Regular setters on purpose,
    /// see <see cref="ConfirmTimeoutSeconds"/>.
    /// </summary>
    public bool HasDuckingMemory { get; set; }

    /// <summary>The ducking value from before a profile that disables ducking; only meaningful with <see cref="HasDuckingMemory"/>.</summary>
    public int? DuckingBeforeProfiles { get; set; }
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

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
