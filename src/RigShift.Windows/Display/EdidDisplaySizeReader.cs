using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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
        if (Edid.Read(display.TargetDevicePath, _log) is not { } edid)
        {
            _log.Debug("No EDID for {Display}", DisplayNames.Of(display));
            return null;
        }

        ScreenSize? size = Edid.PictureSize(edid);
        _log.Debug("EDID of {Display}: {Width} x {Height} mm", display.FriendlyName, size?.WidthMm, size?.HeightMm);
        return size;
    }
}

/// <summary>The few EDID bytes the app needs; the parsing is pure so it is testable.</summary>
public static class Edid
{
    private const string EnumDisplay = @"SYSTEM\CurrentControlSet\Enum\DISPLAY\";

    /// <summary>
    /// The EDID Windows keeps for the monitor behind <paramref name="targetDevicePath"/>; <c>null</c> when the path names no
    /// monitor instance, there is no EDID or it cannot be read.
    /// </summary>
    public static byte[]? Read(string targetDevicePath, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        if (RegistryKeyFor(targetDevicePath) is not { } keyPath)
        {
            return null;
        }

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(keyPath);
            return key?.GetValue("EDID") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "EDID under {Key} could not be read", keyPath);
            return null;
        }
    }

    /// <summary>
    /// Fingerprint of the serial number: the 32-bit number (bytes 12–15) and the text of the serial number descriptor (tag
    /// 0xFF), hashed and cut to 16 hex digits. <c>null</c> when the monitor reports neither. Only ever compared: a filler
    /// value that several monitors share simply tells them no more apart than their model does.
    /// </summary>
    public static string? SerialHash(byte[] edid)
    {
        ArgumentNullException.ThrowIfNull(edid);
        if (edid.Length < 128)
        {
            return null;
        }

        uint number = BinaryPrimitives.ReadUInt32LittleEndian(edid.AsSpan(12, 4));
        string text = DescriptorText(edid, 0xFF);
        if (number == 0 && text.Length == 0)
        {
            return null;
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{number:X8}|{text}")));
        return Convert.ToHexString(hash, 0, 8);
    }

    /// <summary>The text of the first display descriptor with <paramref name="tag"/>, up to its line feed; empty without one.</summary>
    private static string DescriptorText(byte[] edid, byte tag)
    {
        for (int offset = 54; offset <= 108; offset += 18)
        {
            if (edid[offset] == 0 && edid[offset + 1] == 0 && edid[offset + 2] == 0 && edid[offset + 3] == tag)
            {
                ReadOnlySpan<byte> text = edid.AsSpan(offset + 5, 13);
                int end = text.IndexOf((byte)0x0A);
                return Encoding.ASCII.GetString(end >= 0 ? text[..end] : text).Trim();
            }
        }

        return string.Empty;
    }

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
