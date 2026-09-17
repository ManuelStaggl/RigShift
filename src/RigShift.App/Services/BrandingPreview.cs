#if DEBUG
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using RigShift.App.Controls;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.Services;

/// <summary>Debug builds only (<c>--preview-branding &lt;folder&gt;</c>): tray icon sheet plus the tray popup in a window.</summary>
internal static class BrandingPreview
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48];

    public static void Show(string directory, TrayPopupViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        Directory.CreateDirectory(directory);

        // Show the active row style even on a machine where no profile matches the connected displays.
        if (viewModel.Catalog.Items.LastOrDefault() is { } item)
        {
            item.IsActive = true;
        }

        // RIGSHIFT_PREVIEW_TRAY=busy: the popup during a switch, which no screenshot on this server could produce
        // otherwise – applying a profile needs the real displays.
        if (string.Equals(Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_TRAY"), "busy", StringComparison.OrdinalIgnoreCase)
            && viewModel.Catalog.Items.FirstOrDefault(i => !i.IsActive) is { } target)
        {
            viewModel.Coordinator.SwitchingProfile = target.Profile;
            viewModel.Coordinator.IsSwitching = true;
        }

        WriteSheet(Path.Combine(directory, "tray-icons-dark-taskbar.png"), lightTaskbar: false);
        WriteSheet(Path.Combine(directory, "tray-icons-light-taskbar.png"), lightTaskbar: true);

        new Window
        {
            Title = "RigShift Tray Preview",
            Content = new TrayPopupView(viewModel),
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
        }.Show();
    }

    /// <summary>
    /// Rows: the five profile symbols and the RigShift tray icon; columns: tray sizes for 100–300 % DPI. Every cell goes
    /// through <see cref="System.Drawing.Icon"/> exactly like the real tray icon and is shown enlarged 4×.
    /// </summary>
    private static void WriteSheet(string file, bool lightTaskbar)
    {
        const int Zoom = 4;
        const int Cell = (48 * Zoom) + 16;
        var color = lightTaskbar ? System.Windows.Media.Color.FromRgb(0x0F, 0x17, 0x2A) : System.Windows.Media.Colors.White;
        string[] rows = [.. ProfileIcons.All, "rigshift"];

        using var sheet = new System.Drawing.Bitmap(Sizes.Length * Cell, rows.Length * Cell);
        using System.Drawing.Graphics graphics = System.Drawing.Graphics.FromImage(sheet);
        graphics.Clear(lightTaskbar ? System.Drawing.Color.FromArgb(0xF3, 0xF3, 0xF3) : System.Drawing.Color.FromArgb(0x20, 0x20, 0x20));
        graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = PixelOffsetMode.Half;

        for (int row = 0; row < rows.Length; row++)
        {
            for (int column = 0; column < Sizes.Length; column++)
            {
                int size = Sizes[column];
                using System.Drawing.Icon icon = CreateIcon(rows[row], size, color, lightTaskbar);
                using System.Drawing.Bitmap bitmap = icon.ToBitmap();
                graphics.DrawImage(bitmap, (column * Cell) + 8, (row * Cell) + 8, size * Zoom, size * Zoom);
            }
        }

        sheet.Save(file, ImageFormat.Png);
        Log.Information("Branding preview written to {File}", file);
    }

    private static System.Drawing.Icon CreateIcon(string key, int size, System.Windows.Media.Color color, bool lightTaskbar)
    {
        if (ProfileIconRenderer.Render(key, size, color) is { } bitmap)
        {
            return ProfileIconRenderer.ToIcon(bitmap);
        }

        var uri = new Uri("pack://application:,,,/Assets/Brand/rigshift-tray-" + (lightTaskbar ? "light" : "dark") + ".ico", UriKind.Absolute);
        using Stream stream = Application.GetResourceStream(uri).Stream;
        return new System.Drawing.Icon(stream, size, size);
    }
}
#endif
