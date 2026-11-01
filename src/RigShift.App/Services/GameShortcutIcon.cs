using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigShift.Windows.Shell;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The icon of a game's desktop shortcut: the game's own icon in front of a card in the RigShift gradient that shows
/// behind it at the top right. The game stays whole and recognisable; the card says the shortcut goes through RigShift.
/// </summary>
internal static class GameShortcutIcon
{
    /// <summary>The shell's sizes up to 200 % scaling. 16 px has no room for the card and keeps the plain game icon.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private const int SmallestWithCard = 20;

    /// <summary>Share of the edge the game and the card each take.</summary>
    private const double Scale = 0.8;

    private static readonly Brush CardBrush = Frozen(new LinearGradientBrush(
        [
            new GradientStop(Color.FromRgb(0x00, 0xA6, 0xF6), 0),
            new GradientStop(Color.FromRgb(0x00, 0x78, 0xD4), 0.55),
            new GradientStop(Color.FromRgb(0x00, 0x47, 0xED), 1),
        ],
        new Point(0, 0),
        new Point(1, 1)));

    private static readonly Brush RimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)));

    /// <summary>The composed icon for <paramref name="gameId"/>, written next to the other RigShift data.</summary>
    /// <param name="iconFile">The file holding the game's icon, as <c>GameIconSource.Find</c> returns it.</param>
    /// <returns>The <c>.ico</c> path, or <c>null</c> when the game's icon could not be read – then the shortcut takes the file itself.</returns>
    public static string? Create(Guid gameId, string iconFile, string directory, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(iconFile);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            var frames = new List<(int Size, byte[] Png)>(Sizes.Length);
            foreach (int size in Sizes)
            {
                if (Extract(iconFile, size) is not { } source)
                {
                    log.Warning("Icon of {File} could not be read at {Size} px, the shortcut keeps the plain game icon", Path.GetFileName(iconFile), size);
                    return null;
                }

                frames.Add((size, Encode(size < SmallestWithCard ? Plain(source, size) : Stacked(source, size), size)));
            }

            Directory.CreateDirectory(directory);
            string file = PathFor(gameId, directory);
            File.WriteAllBytes(file, IcoFile.Build(frames));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or COMException or InvalidOperationException)
        {
            log.Warning(ex, "Shortcut icon for {File} could not be composed, the shortcut keeps the plain game icon", Path.GetFileName(iconFile));
            return null;
        }
    }

    /// <summary>Removes the icon of a deleted game; its shortcut, if the user kept one, falls back to a blank icon.</summary>
    public static void Delete(Guid gameId, string directory, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(log);

        string file = PathFor(gameId, directory);
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log.Warning(ex, "Shortcut icon {File} could not be removed", file);
        }
    }

    private static string PathFor(Guid gameId, string directory) =>
        Path.Combine(directory, gameId.ToString("N") + ".ico");

    /// <summary>The game's icon frame closest to <paramref name="size"/>, scaled to it by the shell.</summary>
    private static BitmapSource? Extract(string file, int size)
    {
        using System.Drawing.Icon? icon = System.Drawing.Icon.ExtractIcon(file, 0, size);
        if (icon is null)
        {
            return null;
        }

        BitmapSource image = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        image.Freeze();
        return image;
    }

    private static DrawingVisual Plain(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using DrawingContext context = visual.RenderOpen();
        context.DrawImage(source, new Rect(0, 0, size, size));
        return visual;
    }

    private static DrawingVisual Stacked(BitmapSource source, int size)
    {
        // Whole pixels, so the edges stay sharp at 20 and 24 px.
        double edge = Math.Round(size * Scale);
        var card = new Rect(size - edge, 0, edge, edge);
        var game = new Rect(0, size - edge, edge, edge);
        double outline = Math.Max(1, Math.Round(size / 80.0));

        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using DrawingContext context = visual.RenderOpen();

        context.DrawRoundedRectangle(CardBrush, null, card, edge * 0.12, edge * 0.12);

        // A dark rim keeps blue game icons apart from the card; only on the card, a rim over nothing would show as grey.
        var rim = new Rect(game.X, game.Y - outline, game.Width + outline, game.Height + outline);
        context.PushClip(new RectangleGeometry(card, edge * 0.12, edge * 0.12));
        context.DrawRoundedRectangle(RimBrush, null, rim, edge * 0.1, edge * 0.1);
        context.Pop();

        context.PushClip(new RectangleGeometry(game, edge * 0.1, edge * 0.1));
        context.DrawImage(source, game);
        context.Pop();
        return visual;
    }

    private static byte[] Encode(Visual visual, int size)
    {
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
