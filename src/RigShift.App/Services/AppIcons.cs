using System.Collections.Concurrent;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace RigShift.App.Services;

/// <summary>Program icons for the app picker, the editor and the profile cards; frozen, so they can be made off the UI thread.</summary>
internal static class AppIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>The file's icon, or <c>null</c> for a path that is not a file with one.</returns>
    public static ImageSource? Load(string? path)
    {
        string file = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(file))
        {
            return null;
        }

        return Cache.GetOrAdd(file, Extract);
    }

    private static ImageSource? Extract(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

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
