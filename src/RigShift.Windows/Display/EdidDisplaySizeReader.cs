using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Fov;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Windows.Display;

/// <summary>
/// Reads the picture size from the EDID Windows keeps under
/// <c>HKLM\SYSTEM\CurrentControlSet\Enum\DISPLAY\&lt;pnp&gt;\&lt;instance&gt;\Device Parameters</c>. The two path parts
/// are inside the monitor's device interface path, so no SetupAPI call is needed; HKLM reads work without admin rights.
/// </summary>
public sealed class EdidDisplaySizeReader : IDisplaySizeReader
{
    private readonly ILogger _log;

    public EdidDisplaySizeReader(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<EdidDisplaySizeReader>();
    }

    public ScreenSize? Read(DisplayIdentity display)
    {
        ArgumentNullException.ThrowIfNull(display);
        string? keyPath = Edid.RegistryKeyFor(display.TargetDevicePath);
        if (keyPath is null)
        {
            _log.Debug("No registry instance in display path {Path}", display.TargetDevicePath);
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
            if (key?.GetValue("EDID") is not byte[] edid)
            {
                _log.Debug("No EDID under {Key}", keyPath);
                return null;
            }

            ScreenSize? size = Edid.PictureSize(edid);
            _log.Debug("EDID of {Display}: {Width} x {Height} mm", display.FriendlyName, size?.WidthMm, size?.HeightMm);
            return size;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "EDID of {Display} could not be read", display.FriendlyName);
            return null;
        }
    }
}

/// <summary>The few EDID bytes the app needs; pure so the parsing is testable.</summary>
public static class Edid
{
    private const string EnumDisplay = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\";

    /// <summary>
    /// <c>\\?\DISPLAY#XEC2389#4&amp;2f6eb3e3&amp;0&amp;UID20531#{guid}</c> → <c>SYSTEM\...\DISPLAY\XEC2389\4&amp;2f6eb3e3&amp;0&amp;UID20531\Device Parameters</c>.
    /// </summary>
    public static string? RegistryKeyFor(string targetDevicePath)
    {
        if (string.IsNullOrEmpty(targetDevicePath))
        {
            return null;
        }

        string[] parts = targetDevicePath.Split('#');
        if (parts.Length < 3 || !parts[0].EndsWith("DISPLAY", StringComparison.OrdinalIgnoreCase)
            || parts[1].Length == 0 || parts[2].Length == 0 || parts[1].Contains('\\', StringComparison.Ordinal) || parts[2].Contains('\\', StringComparison.Ordinal))
        {
            return null;
        }

        return EnumDisplay + parts[1] + '\\' + parts[2] + @"\Device Parameters";
    }

    /// <summary>
    /// Picture size in millimetres: the first detailed timing descriptor (bytes 66–68) carries it to the millimetre,
    /// the basic block (bytes 21/22) only to the centimetre. Zero in both means the display does not say.
    /// </summary>
    public static ScreenSize? PictureSize(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        if (edid.Length < 128)
        {
            return null;
        }

        int widthMm = edid[66] | ((edid[68] & 0xF0) << 4);
        int heightMm = edid[67] | ((edid[68] & 0x0F) << 8);
        if (widthMm > 0 && heightMm > 0)
        {
            return new ScreenSize(widthMm, heightMm);
        }

        int widthCm = edid[21];
        int heightCm = edid[22];
        return widthCm > 0 && heightCm > 0 ? new ScreenSize(widthCm * 10, heightCm * 10) : null;
    }
}
