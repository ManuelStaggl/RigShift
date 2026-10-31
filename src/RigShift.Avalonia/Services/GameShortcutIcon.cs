using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using RigShift.Windows.Shell;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>
/// The icon of a game's desktop shortcut, drawn with GDI+ like the WPF app's: the game's own icon in front of a card in
/// the RigShift gradient that shows behind it at the top right. The game stays whole and recognisable; the card says
/// the shortcut goes through RigShift.
/// </summary>
internal static class GameShortcutIcon
{
    /// <summary>The shell's sizes up to 200 % scaling. 16 px has no room for the card and keeps the plain game icon.</summary>
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    private const int SmallestWithCard = 20;

    /// <summary>Share of the edge the game and the card each take.</summary>
    private const float Scale = 0.8f;

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
                using Icon? icon = Icon.ExtractIcon(iconFile, 0, size);
                if (icon is null)
                {
                    log.Warning("Icon of {File} could not be read at {Size} px, the shortcut keeps the plain game icon", Path.GetFileName(iconFile), size);
                    return null;
                }

                using Bitmap source = icon.ToBitmap();
                frames.Add((size, Draw(source, size)));
            }

            Directory.CreateDirectory(directory);
            string file = PathFor(gameId, directory);
            File.WriteAllBytes(file, IcoFile.Build(frames));
            return file;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or COMException or ExternalException or InvalidOperationException)
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

    /// <summary>One frame as PNG: the plain icon below 20 px, else game and card stacked.</summary>
    private static byte[] Draw(Bitmap source, int size)
    {
        using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            if (size < SmallestWithCard)
            {
                g.DrawImage(source, 0, 0, size, size);
            }
            else
            {
                Stacked(g, source, size);
            }
        }

        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private static void Stacked(Graphics g, Bitmap source, int size)
    {
        // Whole pixels, so the edges stay sharp at 20 and 24 px.
        float edge = MathF.Round(size * Scale);
        var card = new RectangleF(size - edge, 0, edge, edge);
        var game = new RectangleF(0, size - edge, edge, edge);
        float outline = Math.Max(1, MathF.Round(size / 80f));

        using GraphicsPath cardPath = Rounded(card, edge * 0.12f);
        using (var brush = new LinearGradientBrush(card, Color.FromArgb(0x00, 0xA6, 0xF6), Color.FromArgb(0x00, 0x47, 0xED), LinearGradientMode.ForwardDiagonal))
        {
            brush.InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0x00, 0xA6, 0xF6), Color.FromArgb(0x00, 0x78, 0xD4), Color.FromArgb(0x00, 0x47, 0xED)],
                Positions = [0f, 0.55f, 1f],
            };
            g.FillPath(brush, cardPath);
        }

        // A dark rim keeps blue game icons apart from the card; only on the card, a rim over nothing would show as grey.
        var rim = new RectangleF(game.X, game.Y - outline, game.Width + outline, game.Height + outline);
        g.SetClip(cardPath);
        using (GraphicsPath rimPath = Rounded(rim, edge * 0.1f))
        using (var rimBrush = new SolidBrush(Color.FromArgb(0x59, 0, 0, 0)))
        {
            g.FillPath(rimBrush, rimPath);
        }

        using GraphicsPath gamePath = Rounded(game, edge * 0.1f);
        g.SetClip(gamePath);
        g.DrawImage(source, game);
        g.ResetClip();
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        float d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
