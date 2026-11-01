#if DEBUG
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigShift.App.Views.Debug;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Debug builds only (<c>--preview-gallery &lt;folder&gt;</c>): renders the component gallery to <c>gallery.png</c>
/// without a window, so it works over RDP and needs no screenshot of the desktop.
/// </summary>
internal static class GalleryPreview
{
    public static void Write(string directory)
    {
        Directory.CreateDirectory(directory);
        var view = new GalleryView();
        view.Measure(new Size(view.Width, double.PositiveInfinity));
        view.Arrange(new Rect(0, 0, view.Width, view.DesiredSize.Height));
        view.UpdateLayout();

        // 2× for a close look at 1 px lines and 12 px text.
        const double Scale = 2;
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(view.ActualWidth * Scale), (int)Math.Ceiling(view.ActualHeight * Scale), 96 * Scale, 96 * Scale, PixelFormats.Pbgra32);
        bitmap.Render(view);

        string file = Path.Combine(directory, "gallery.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(file);
        encoder.Save(stream);
        Log.Information("Gallery preview written to {File} ({Width}×{Height})", file, view.ActualWidth, view.ActualHeight);
    }
}
#endif
