using System.ComponentModel;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Serilog;

namespace RigShift.UI.Services;

/// <summary>
/// Program icons for <see cref="RigShift.App.Services.AppIcons"/>: extracted off the UI thread as PNG bytes, because a
/// WinUI image can only be made on the UI thread; <see cref="Image"/> turns them into one where they are shown.
/// </summary>
public static class WinUiIcons
{
    /// <summary>Large enough for a 56 px tile at 150 %.</summary>
    private const int Size = 96;

    public static object? Extract(string file)
    {
        try
        {
            // The executable's own icon at the size asked for; other files only have their associated 32 px icon.
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractIcon(file, 0, Size) ?? System.Drawing.Icon.ExtractAssociatedIcon(file);
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

    /// <summary>For <c>x:Bind</c>: the image of icon bytes from <see cref="Extract"/>, or null.</summary>
    public static ImageSource? Image(object? icon)
    {
        if (icon is not byte[] png)
        {
            return null;
        }

        var image = new BitmapImage();
        using var stream = new MemoryStream(png);
        image.SetSource(stream.AsRandomAccessStream());
        return image;
    }
}
