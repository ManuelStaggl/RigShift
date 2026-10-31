using System.Globalization;
using Microsoft.Win32;
using Serilog;

namespace RigShift.App.Services;

/// <summary>Windows' accessibility setting "Text size" (Settings → Accessibility, 100–225 %).</summary>
public static class TextScaleSetting
{
    private const double Largest = 2.25;
    private const string KeyPath = @"Software\Microsoft\Accessibility";
    private const string ValueName = "TextScaleFactor";

    /// <summary>
    /// The factor a window grows by: the setting, never below 1, and no larger than lets the window's smallest size
    /// still fit the screen – a layout cut off at the edge helps nobody who needs large text.
    /// </summary>
    public static double Factor(int? percent, double roomWidth, double roomHeight, double minWidth, double minHeight)
    {
        double factor = Math.Clamp((percent ?? 100) / 100d, 1, Largest);
        if (minWidth > 0)
        {
            factor = Math.Min(factor, roomWidth / minWidth);
        }

        if (minHeight > 0)
        {
            factor = Math.Min(factor, roomHeight / minHeight);
        }

        return Math.Max(1, factor);
    }

    /// <returns>The percentage, or <c>null</c> when it was never changed or cannot be read.</returns>
    public static int? Read()
    {
#if DEBUG
        // Screenshots of the scaled layout without touching the Windows setting.
        if (int.TryParse(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TEXTSCALE"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int preview))
        {
            return preview;
        }
#endif
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) as int?;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            Log.Warning(ex, "Text size setting not readable, windows keep their normal size");
            return null;
        }
    }
}
