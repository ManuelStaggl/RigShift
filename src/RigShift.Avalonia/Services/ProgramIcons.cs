using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>Program icons as PNG bytes, like the WinUI app's; <see cref="Views.Converters.IconImage"/> turns them into a bitmap.</summary>
internal static class ProgramIcons
{
    public static object? Extract(string file)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractIcon(file, 0, 96) ?? System.Drawing.Icon.ExtractAssociatedIcon(file);
            if (icon is null)
            {
                return null;
            }

            using System.Drawing.Bitmap bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return stream.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or COMException or ExternalException)
        {
            Log.Debug("No icon for {File}: {Reason}", Path.GetFileName(file), ex.Message);
            return null;
        }
    }
}
