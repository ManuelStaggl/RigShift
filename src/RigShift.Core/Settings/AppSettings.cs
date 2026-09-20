using System.Globalization;
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

    /// <summary>
    /// Profile marked as default. Applying it at startup was removed (user decision O-03, it competed with the USB
    /// automation); older files with <c>applyDefaultProfileOnStartup</c> still load, the key is ignored.
    /// </summary>
    public Guid? DefaultProfileId { get; init; }

    /// <summary>System-wide key combination for "back to the previous profile" (1.7.0); <c>null</c> = none.</summary>
    public Profiles.Hotkey? ToggleHotkey { get; init; }

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
    /// carry the name too, so the core and the command line do not need the settings.
    /// </summary>
    public IReadOnlyDictionary<string, string>? DisplayNames { get; init; }

    /// <summary>Refresh rates displays offered once, for the profile editor (<see cref="Profiles.RefreshRateMemory"/>, finding HW-13).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? RefreshRates { get; init; }

    /// <summary>Custom USB device names by <c>VID_xxxx&amp;PID_xxxx</c> (user decision U-01, <see cref="Automation.UsbDeviceNames"/>).</summary>
    public IReadOnlyDictionary<string, string>? UsbDeviceNames { get; init; }

    /// <summary>USB device rules of the automation page.</summary>
    public IReadOnlyList<AutomationRule>? AutomationRules { get; init; }

    /// <summary>
    /// The setup assistant opened once; it starts by itself only on a first start without profiles. <c>false</c> is the
    /// default for files without the key, which is right: those users have profiles already.
    /// </summary>
    public bool SetupAssistantShown { get; init; }

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

    /// <summary>Eye distance the field-of-view dialog last used, in centimetres; <c>null</c> = the dialog's default.</summary>
    public int? FovDistanceCm { get; init; }

    /// <summary>Frame width the field-of-view dialog last used for triples, in millimetres; <c>null</c> = the dialog's default.</summary>
    public int? FovBezelMm { get; init; }

    /// <summary>The field-of-view page was last used for three screens. <c>false</c> is the default for files without the key.</summary>
    public bool FovTriple { get; init; }

    /// <summary>Side angle the user measured, in whole degrees; <c>null</c> lets the page calculate the ideal one.</summary>
    public int? FovAngleDegrees { get; init; }

    /// <summary>The comfort slack is switched on. <c>false</c> is the default for files without the key.</summary>
    public bool FovComfort { get; init; }

    /// <summary>Curvature radius in millimetres per display, keyed by its target device path; a missing entry is flat.</summary>
    public IReadOnlyDictionary<string, int>? FovCurvatureMm { get; init; }

    /// <summary>How far the eye sits above the middle of the screen, in centimetres; only used for the hint it prints.</summary>
    public int? FovVerticalOffsetCm { get; init; }

    public const int MaxConfirmTimeoutSeconds = 120;

    /// <summary>
    /// The settings with everything a hand-edited or half-written file can get wrong put right: numbers inside the
    /// ranges the pages offer, no <c>null</c> entries in lists and tables, a language that exists. Returns the same
    /// instance when there is nothing to fix, so loading a good file changes nothing.
    /// </summary>
    public AppSettings Sanitized()
    {
        IReadOnlyList<AutomationRule>? rules = AutomationRules is { } list && list.Any(NeedsFixing)
            ? [.. list.Where(r => r is not null).Select(Fixed)]
            : AutomationRules;

        var fixedUp = this with
        {
            ConfirmTimeoutSeconds = Math.Clamp(ConfirmTimeoutSeconds, 0, MaxConfirmTimeoutSeconds),
            Language = IsCulture(Language) ? Language : null,
            FovDistanceCm = Clamp(FovDistanceCm, 20, 300),
            FovBezelMm = Clamp(FovBezelMm, 0, 100),
            FovAngleDegrees = Clamp(FovAngleDegrees, 0, 89),
            FovVerticalOffsetCm = Clamp(FovVerticalOffsetCm, 0, 60),
            FovCurvatureMm = FovCurvatureMm is { } radii && radii.Any(r => r.Value is < 300 or > 5000)
                ? radii.ToDictionary(r => r.Key, r => Math.Clamp(r.Value, 300, 5000), StringComparer.OrdinalIgnoreCase)
                : FovCurvatureMm,
            DisplayNames = WithoutNulls(DisplayNames),
            UsbDeviceNames = WithoutNulls(UsbDeviceNames),
            RefreshRates = RefreshRates is { } rates && rates.Any(r => r.Value is null)
                ? rates.Where(r => r.Value is not null).ToDictionary(r => r.Key, r => r.Value, StringComparer.OrdinalIgnoreCase)
                : RefreshRates,
            AutomationRules = rules,
        };

        // Record equality compares the tables by reference, and those only changed when something was fixed.
        return fixedUp == this ? this : fixedUp;
    }

    private static bool NeedsFixing(AutomationRule? rule) =>
        rule is null
        || rule.ExitDelaySeconds is < 0 or > AutomationRule.MaxExitDelaySeconds
        || rule.Devices?.Any(d => d is null) == true;

    private static AutomationRule Fixed(AutomationRule rule) => rule with
    {
        ExitDelaySeconds = Math.Clamp(rule.ExitDelaySeconds, 0, AutomationRule.MaxExitDelaySeconds),
        Devices = rule.Devices?.Any(d => d is null) == true ? [.. rule.Devices.Where(d => d is not null)] : rule.Devices,
    };

    private static int? Clamp(int? value, int min, int max) => value is { } number ? Math.Clamp(number, min, max) : null;

    private static IReadOnlyDictionary<string, string>? WithoutNulls(IReadOnlyDictionary<string, string>? table) =>
        table is not null && table.Any(e => e.Value is null)
            ? table.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value, StringComparer.OrdinalIgnoreCase)
            : table;

    private static bool IsCulture(string? name)
    {
        if (name is null)
        {
            return true;
        }

        try
        {
            // predefinedOnly: Windows otherwise accepts any well-formed tag, and "xx-nonsense" is one.
            return name.Length > 0 && CultureInfo.GetCultureInfo(name, predefinedOnly: true) is not null;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }
}

/// <summary>What went wrong when the settings were last read.</summary>
public enum SettingsLoadProblem
{
    None,

    /// <summary>The file is not valid JSON; defaults are in use.</summary>
    Damaged,

    /// <summary>The file could not be opened (locked, no access); defaults are in use.</summary>
    Unreadable,

    /// <summary>A newer RigShift wrote the file; saving drops what this version does not know.</summary>
    FromNewerVersion,
}

/// <param name="BackupFile">The copy of the file as it was found, or <c>null</c> when none was (or could be) made.</param>
public sealed record SettingsLoadReport(SettingsLoadProblem Problem, string? BackupFile)
{
    public static SettingsLoadReport Fine { get; } = new(SettingsLoadProblem.None, null);
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/>. A missing or unreadable file yields defaults, never an exception – but a
/// file that was there and could not be used is copied aside before anything is written over it, and
/// <see cref="LastLoad"/> says so.
/// </summary>
public sealed class JsonSettingsStore
{
    private readonly string _file;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    /// <summary>The file could not be copied when it was read (it was locked); tried again before the first save.</summary>
    private bool _copyOwed;

    public JsonSettingsStore(string file, ILogger log, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(log);
        _file = Path.GetFullPath(file);
        _log = log.ForContext<JsonSettingsStore>();
        _time = time ?? TimeProvider.System;
    }

    /// <summary>How the last <see cref="LoadAsync"/> went.</summary>
    public SettingsLoadReport LastLoad { get; private set; } = SettingsLoadReport.Fine;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        _copyOwed = false;
        LastLoad = SettingsLoadReport.Fine;
        if (!File.Exists(_file))
        {
            return new AppSettings();
        }

        try
        {
            AppSettings? settings;
            await using (FileStream stream = File.OpenRead(_file))
            {
                settings = await JsonSerializer.DeserializeAsync(stream, SettingsJsonContext.Default.AppSettings, cancellationToken);
            }

            if (settings is null)
            {
                return Unusable(SettingsLoadProblem.Damaged, "corrupt", null);
            }

            if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
            {
                _log.Warning("Settings file {File} has schema {Schema}, this version knows {Known}; keeping a copy before it is rewritten",
                    _file, settings.SchemaVersion, AppSettings.CurrentSchemaVersion);
                LastLoad = new SettingsLoadReport(
                    SettingsLoadProblem.FromNewerVersion,
                    CopyAside(string.Create(CultureInfo.InvariantCulture, $"v{settings.SchemaVersion}")));
            }

            AppSettings sanitized = settings.Sanitized();
            if (!ReferenceEquals(sanitized, settings))
            {
                _log.Warning("Settings file {File} held values out of range or empty entries; they were put right", _file);
            }

            return MigrateRules(sanitized);
        }
        catch (JsonException ex)
        {
            return Unusable(SettingsLoadProblem.Damaged, "corrupt", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Unusable(SettingsLoadProblem.Unreadable, "corrupt", ex);
        }
    }

    private AppSettings Unusable(SettingsLoadProblem problem, string tag, Exception? ex)
    {
        _log.Error(ex, "Settings file {File} could not be read ({Problem}), using defaults", _file, problem);
        string? copy = CopyAside(tag);
        _copyOwed = copy is null;
        LastLoad = new SettingsLoadReport(problem, copy);
        return new AppSettings();
    }

    /// <summary>Copies the file next to itself as <c>settings.&lt;tag&gt;-&lt;date&gt;.json</c>; <c>null</c> when that fails.</summary>
    private string? CopyAside(string tag)
    {
        string stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string copy = Path.Combine(
            Path.GetDirectoryName(_file) ?? ".",
            $"{Path.GetFileNameWithoutExtension(_file)}.{tag}-{stamp}{Path.GetExtension(_file)}");
        try
        {
            File.Copy(_file, copy, overwrite: true);
            _log.Warning("Kept the settings file as {Copy}", copy);
            return copy;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "The settings file could not be copied to {Copy}", copy);
            return null;
        }
    }

    /// <summary>
    /// Rules have no switch of their own any more (analysis finding O-07). A rule switched off in 1.3.x would start switching
    /// if it simply came back on, so it is dropped with a warning; the next save removes it from the file. The single device
    /// of 1.3.x becomes the rule's device list (U-02).
    /// </summary>
    private AppSettings MigrateRules(AppSettings settings)
    {
        if (settings.AutomationRules is not { } rules
            || rules.All(r => r.LegacyIsEnabled is null && r.LegacyUsbDeviceId is null && r.LegacyUsbDeviceName is null))
        {
            return settings;
        }

        var kept = new List<AutomationRule>(rules.Count);
        foreach (AutomationRule rule in rules)
        {
            if (rule.LegacyIsEnabled == false)
            {
                _log.Warning(
                    "Automation rule {Rule} for {Device} was switched off in an earlier version and is removed, because rules no longer have their own switch",
                    rule.Id,
                    Automation.UsbDeviceNames.Describe(rule, settings.UsbDeviceNames));
                continue;
            }

            kept.Add(rule.Migrated() with { LegacyIsEnabled = null });
        }

        return settings with { AutomationRules = kept };
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Directory.CreateDirectory(Path.GetDirectoryName(_file) ?? ".");

        // The file was locked when it was read, so defaults are about to replace settings nobody has seen. Whoever held
        // it is most likely gone by now.
        if (_copyOwed && File.Exists(_file))
        {
            string? copy = CopyAside("corrupt");
            _copyOwed = false;
            LastLoad = LastLoad with { BackupFile = copy };
        }

        await Storage.AtomicFile.WriteAsync(
            _file,
            stream => JsonSerializer.SerializeAsync(
                stream, settings with { SchemaVersion = AppSettings.CurrentSchemaVersion }, SettingsJsonContext.Default.AppSettings, cancellationToken),
            cancellationToken);
        _log.Information("Settings saved to {File}", _file);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
