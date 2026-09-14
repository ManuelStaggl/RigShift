using System.Text.RegularExpressions;

namespace RigShift.Core.Automation;

/// <summary>A connected USB device the user can pick for a rule.</summary>
public sealed record UsbDevice(string Id, string Name);

/// <summary>
/// USB devices are matched by vendor and product id (<c>VID_046D&amp;PID_C24F</c>), so a device keeps matching in another
/// port (docs/PLAN.md, section 6, item 7).
/// </summary>
public static partial class UsbDeviceIds
{
    private const string KeyPrefix = "usb:";

    /// <summary>
    /// <c>USB\VID_046D&amp;PID_C547&amp;LAMPARRAY\5&amp;1a2b</c> → <c>VID_046D&amp;PID_C547</c>; <c>null</c> without vendor and product id.
    /// </summary>
    public static string? Normalize(string? instanceOrDeviceId)
    {
        if (string.IsNullOrWhiteSpace(instanceOrDeviceId))
        {
            return null;
        }

        Match match = VidPid().Match(instanceOrDeviceId);
        return match.Success
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"VID_{match.Groups[1].Value.ToUpperInvariant()}&PID_{match.Groups[2].Value.ToUpperInvariant()}")
            : null;
    }

    /// <summary>
    /// The key a present device has in the set the automation evaluates. Process names cannot contain a colon, so the
    /// prefix keeps devices and programs apart.
    /// </summary>
    public static string Key(string deviceId) => KeyPrefix + deviceId.ToUpperInvariant();

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex VidPid();
}
