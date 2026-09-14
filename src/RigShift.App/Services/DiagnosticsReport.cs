using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.App.Services;

/// <summary>Everything the diagnostic report is built from. An error text replaces a part that could not be read.</summary>
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
    IReadOnlyList<SwitchRecord> History);

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
                $"- {profile.Name}{(profile.Id == input.ActiveProfileId ? " [active]" : string.Empty)}: {displays}; confirmation {(profile.SwitchWithoutAsking ? "off" : "app setting")}, {profile.Apps.Count} app(s)"));
        }

        text.AppendLine().AppendLine("## Recent switches");
        if (input.History.Count == 0)
        {
            text.AppendLine("None since RigShift started.");
        }

        foreach (SwitchRecord record in input.History)
        {
            text.AppendLine(FormattableString.Invariant(
                $"- {record.At:yyyy-MM-dd HH:mm:ss} {record.ProfileName}: {record.Outcome}, {record.Attempts} attempt(s), {record.Duration.TotalSeconds:0.0} s, audio {record.Audio}, apps {record.Apps}{(record.NativeError is { } error ? $", error {error}" : string.Empty)}{(record.HasDetails ? $" – {record.Details}" : string.Empty)}"));
        }

        return Anonymize(text.ToString(), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>
    /// Replaces the user's profile folder with <c>%USERPROFILE%</c> and any other <c>\Users\&lt;name&gt;</c> segment
    /// with <c>\Users\&lt;user&gt;</c> – messages can carry app paths, and the report is meant for a public issue.
    /// </summary>
    internal static string Anonymize(string report, string? userProfile)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!string.IsNullOrEmpty(userProfile))
        {
            report = report.Replace(userProfile.TrimEnd('\\'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        }

        return UsersSegment().Replace(report, "<user>");
    }

    [GeneratedRegex(@"(?<=\\Users\\)[^\\/\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex UsersSegment();

    private static void AppendDisplay(StringBuilder text, AttachedDisplay display, string? customName)
    {
        string state = display.IsActive ? "active" : display.IsAvailable ? "connected, off" : "not ready";
        text.Append(FormattableString.Invariant($"- {DisplayNames.Label(customName, display.Identity, "unnamed display")}: {state}"));
        if (display.ActiveMode is { } mode)
        {
            double hertz = mode.RefreshDenominator == 0 ? 0 : (double)mode.RefreshNumerator / mode.RefreshDenominator;
            text.Append(FormattableString.Invariant(
                $", {mode.Width}x{mode.Height} @ {hertz:0.##} Hz at ({mode.PositionX}, {mode.PositionY}){(mode.IsPrimary ? ", primary" : string.Empty)}"));
        }

        text.AppendLine();
        text.AppendLine(FormattableString.Invariant($"  EDID {display.Identity.EdidManufacturerId:X4}:{display.Identity.EdidProductCodeId:X4}"));
        text.AppendLine(FormattableString.Invariant($"  target {display.Identity.TargetDevicePath}"));
        text.AppendLine(FormattableString.Invariant($"  adapter {display.Identity.AdapterDevicePath}"));
    }

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
