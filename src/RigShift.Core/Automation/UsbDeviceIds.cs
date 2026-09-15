using System.Text.RegularExpressions;

namespace RigShift.Core.Automation;

/// <summary>A connected USB device the user can pick for a rule.</summary>
public sealed record UsbDevice(string Id, string Name);

/// <summary>
/// USB devices are matched by vendor and product id (<c>VID_046D&amp;PID_C24F</c>), so a device keeps matching in another
/// port.
/// </summary>
public static partial class UsbDeviceIds
{
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

    [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 250)]
    private static partial Regex VidPid();
}
