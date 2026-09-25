using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Topology;
using RigShift.Windows.Display;

namespace RigShift.App.Services;

/// <summary>Everything the diagnostic report is built from. An error text replaces a part that could not be read.</summary>
/// <param name="TextScalePercent">The Windows "Text size", <c>null</c> when it was never changed.</param>
public sealed record DiagnosticsInput(
    string Version,
    bool IsInstalled,
    DisplaySnapshot? Snapshot,
    string? DisplayError,
    IReadOnlyList<AudioDeviceInfo> Playback,
    IReadOnlyList<AudioDeviceInfo> Recording,
    string? AudioError,
    IReadOnlyList<Profile> Profiles,
    Guid? ActiveProfileId,
    IReadOnlyDictionary<string, string> DisplayNames,
    IReadOnlyList<SwitchRecord> History,
    IReadOnlyList<GraphicsDriver>? Graphics = null,
    AppSettings? Settings = null,
    int GameCount = 0,
    int? TextScalePercent = null);

/// <summary>
/// Plain-text report to paste into a GitHub issue ("Copy diagnostic info"). English and invariant culture on purpose:
/// maintainers read it. Contains display device paths and device names, no audio endpoint IDs; the user name in paths
/// is replaced (analysis finding H-05).
/// </summary>
public static partial class DiagnosticsReport
{
    public static string Build(DiagnosticsInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var text = new StringBuilder();
        text.AppendLine(FormattableString.Invariant($"RigShift {input.Version}{(input.IsInstalled ? string.Empty : " (development build)")}"));
        text.AppendLine(FormattableString.Invariant($"Windows {Environment.OSVersion.Version} ({RuntimeInformation.OSArchitecture}), .NET {Environment.Version}"));
        text.AppendLine(FormattableString.Invariant($"Created {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}"));

        // The first question in every display issue: which card, which driver (v4 finding E-07).
        text.AppendLine().AppendLine("## Graphics");
        if (input.Graphics is not { Count: > 0 })
        {
            text.AppendLine("Could not be read.");
        }

        foreach (GraphicsDriver driver in input.Graphics ?? [])
        {
            text.AppendLine(FormattableString.Invariant($"- {driver}"));
        }

        if (input.Settings is { } settings)
        {
            AppendSettings(text, settings, input.GameCount, input.TextScalePercent);
        }

        text.AppendLine().AppendLine("## Displays");
        if (input.Snapshot is null)
        {
            text.AppendLine(FormattableString.Invariant($"Could not be read: {input.DisplayError}"));
        }
        else if (input.Snapshot.Displays.Count == 0)
        {
            text.AppendLine("None found.");
        }
        else
        {
            foreach (AttachedDisplay display in input.Snapshot.Displays)
            {
                AppendDisplay(text, display, input.DisplayNames.GetValueOrDefault(display.Identity.TargetDevicePath));
            }
        }

        text.AppendLine().AppendLine("## Audio");
        if (input.AudioError is not null)
        {
            text.AppendLine(FormattableString.Invariant($"Could not be read: {input.AudioError}"));
        }

        AppendAudio(text, "Playback", input.Playback);
        AppendAudio(text, "Recording", input.Recording);

        text.AppendLine().AppendLine("## Profiles");
        if (input.Profiles.Count == 0)
        {
            text.AppendLine("None.");
        }

        foreach (Profile profile in input.Profiles)
        {
            string displays = string.Join(", ", profile.Displays.Select(d =>
                DisplayNames.Of(d) + (d.IsPrimary ? " (primary)" : string.Empty) + (d.IsOptional ? " (optional)" : string.Empty)));
            text.AppendLine(FormattableString.Invariant(
                $"- {profile.Name}{(profile.Id == input.ActiveProfileId ? " [active]" : string.Empty)}: {displays}; confirmation {(profile.SwitchWithoutAsking ? "off" : "app setting")}, {profile.Apps.Count} app(s){(profile.KeepAwake ? ", keeps awake" : string.Empty)}"));
        }

        text.AppendLine().AppendLine("## Recent switches");
        if (input.History.Count == 0)
        {
            text.AppendLine("None since RigShift started.");
        }

        foreach (SwitchRecord record in input.History)
        {
            text.AppendLine(FormattableString.Invariant(
                $"- {record.At:yyyy-MM-dd HH:mm:ss} {record.ProfileName}: {record.Outcome}, {record.Attempts} attempt(s), {record.Duration.TotalSeconds:0.0} s, audio {record.Audio}, apps {record.Apps}{(record.Outcome == SwitchOutcome.Failed && record.NativeError is { } error ? $", error {error}" : string.Empty)}{(record.HasDetails ? $" – {record.Details}" : string.Empty)}"));
        }

        return Anonymize(text.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Replaces the user's profile folder with <c>%USERPROFILE%</c> and any other <c>\Users\&lt;name&gt;</c> segment
    /// with <c>\Users\&lt;user&gt;</c> – messages can carry app paths, and the report is meant for a public issue. Also in
    /// JSON, where every backslash is doubled: the support package carries the profile files.
    /// </summary>
    internal static string Anonymize(string report, string? userProfile)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.IsNullOrEmpty(userProfile))
        {
            string folder = userProfile.TrimEnd('\\');
            report = report
                .Replace(folder.Replace(@"\", @"\\", StringComparison.Ordinal), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
                .Replace(folder, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return UsersSegment().Replace(report, "<user>");
    }

    private static void AppendSettings(StringBuilder text, AppSettings settings, int gameCount, int? textScalePercent)
    {
        int rules = settings.AutomationRules?.Count ?? 0;
        text.AppendLine().AppendLine("## Settings");
        text.AppendLine(FormattableString.Invariant($"Language {settings.Language ?? "Windows"}, text size {textScalePercent ?? 100} %"));
        text.AppendLine(FormattableString.Invariant($"Updates: {(settings.OnlyNotifyAboutUpdates ? "notify only" : "install automatically")}"));
        text.AppendLine(FormattableString.Invariant($"Confirmation: {(settings.ConfirmTimeoutSeconds > 0 ? $"{settings.ConfirmTimeoutSeconds} s" : "off")}"));
        text.AppendLine(FormattableString.Invariant(
            $"Default profile: {(settings.DefaultProfileId is null ? "none" : "set")}, switch-back hotkey: {(settings.ToggleHotkey is null ? "none" : "set")}"));
        text.AppendLine(FormattableString.Invariant($"USB rules: {rules}{(rules > 0 && settings.AutomationPaused ? " (paused)" : string.Empty)}"));
        text.AppendLine(FormattableString.Invariant($"Games: {gameCount}"));
        text.AppendLine(FormattableString.Invariant($"Detailed log: {(settings.DetailedLogging ? "on" : "off")}"));
    }

    // In JSON the backslash after "Users" is doubled.
    [GeneratedRegex(@"(?<=\\Users\\\\?)[^\\/\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UsersSegment();

    private static void AppendDisplay(StringBuilder text, AttachedDisplay display, string? customName)
    {
        string state = display.IsActive ? "active" : display.IsAvailable ? "connected, off" : "not ready";
        text.Append(FormattableString.Invariant($"- {DisplayNames.Label(customName, display.Identity, "unnamed display")}: {state}"));
        if (display.ActiveMode is { } mode)
        {
            double hertz = RefreshRate.Of(mode).Hertz;
            text.Append(FormattableString.Invariant(
                $", {mode.Width}x{mode.Height} @ {hertz:0.##} Hz at ({mode.PositionX}, {mode.PositionY}){(mode.IsPrimary ? ", primary" : string.Empty)}"));
        }

        text.AppendLine();
        // Four digits of the fingerprint are enough to see whether identical monitors report different serial numbers (K-03).
        string serial = display.Identity.EdidSerialHash is { Length: >= 4 } hash ? "serial " + hash[..4] : "no serial number";
        text.AppendLine(FormattableString.Invariant($"  EDID {display.Identity.EdidManufacturerId:X4}:{display.Identity.EdidProductCodeId:X4}, {serial}"));
        text.AppendLine(FormattableString.Invariant($"  target {ShortTargetPath(display.Identity.TargetDevicePath)}"));
        text.AppendLine(FormattableString.Invariant($"  adapter {ShortAdapterPath(display.Identity.AdapterDevicePath)}"));
    }

    /// <summary>
    /// <c>\\?\DISPLAY#AUS32F6#5&amp;…&amp;UID4353#{…}</c> → <c>AUS32F6 · UID4353</c>. The instance ids identify the machine and
    /// help nobody reading an issue (finding HW-17).
    /// </summary>
    internal static string ShortTargetPath(string path)
    {
        string[] parts = path.Split('#');
        if (parts.Length < 2)
        {
            return "(unrecognized)";
        }

        Match uid = UidPart().Match(path);
        return uid.Success ? $"{parts[1]} · {uid.Value}" : parts[1];
    }

    /// <summary><c>\\?\PCI#VEN_10DE&amp;DEV_2702&amp;SUBSYS_…#…</c> → <c>VEN_10DE&amp;DEV_2702</c>; other buses keep their first two parts.</summary>
    internal static string ShortAdapterPath(string path)
    {
        Match device = PciDevicePart().Match(path);
        if (device.Success)
        {
            return device.Value;
        }

        string[] parts = path.Split('#');
        return parts.Length < 2 ? "(unrecognized)" : $"{parts[0].Replace(@"\\?\", string.Empty, StringComparison.Ordinal)}\\{parts[1].Split('&')[0]}";
    }

    [GeneratedRegex(@"UID\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UidPart();

    [GeneratedRegex(@"VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PciDevicePart();

    private static void AppendAudio(StringBuilder text, string direction, IReadOnlyList<AudioDeviceInfo> devices)
    {
        if (devices.Count == 0)
        {
            text.AppendLine(FormattableString.Invariant($"- {direction}: none"));
            return;
        }

        foreach (AudioDeviceInfo device in devices)
        {
            text.AppendLine(FormattableString.Invariant(
                $"- {direction}: {device.Endpoint.FriendlyName} ({(device.IsActive ? "active" : "inactive")}{(device.IsDefault ? ", default" : string.Empty)})"));
        }
    }
}
