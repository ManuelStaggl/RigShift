using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Serilog;

namespace RigShift.App.Services;

/// <summary>The WPF image behind <see cref="AppIcons"/>; frozen, so it can be made off the UI thread.</summary>
internal static class WpfIcons
{
    public static object? Extract(string file)
    {
        try
        {
            using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractAssociatedIcon(file);
            if (icon is null)
            {
                return null;
            }

            BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            Log.Debug("No icon for {File}: {Reason}", Path.GetFileName(file), ex.Message);
            return null;
        }
    }
}
