using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RigShift.Core.Profiles;
using Wpf.Ui.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// A profile symbol (Fluent System Icons from WPF-UI) in the inherited foreground: outlined, or filled for the active
/// profile. Decorative – the profile name next to it carries the meaning. Shows nothing without a known symbol.
/// </summary>
public sealed class ProfileIcon : Decorator
{
    public static readonly DependencyProperty IconKeyProperty = DependencyProperty.Register(
        nameof(IconKey), typeof(string), typeof(ProfileIcon), new PropertyMetadata(null, OnSymbolChanged));

    public static readonly DependencyProperty IsFilledProperty = DependencyProperty.Register(
        nameof(IsFilled), typeof(bool), typeof(ProfileIcon), new PropertyMetadata(false, OnSymbolChanged));

    private readonly SymbolIcon _symbol = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    public ProfileIcon()
    {
        Child = _symbol;
        UpdateSymbol();
    }

    public string? IconKey
    {
        get => (string?)GetValue(IconKeyProperty);
        set => SetValue(IconKeyProperty, value);
    }

    public bool IsFilled
    {
        get => (bool)GetValue(IsFilledProperty);
        set => SetValue(IsFilledProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // The glyph fills the em box, so the font size is the icon size.
        double size = Math.Min(double.IsNaN(Width) ? 24 : Width, double.IsNaN(Height) ? 24 : Height);
        _symbol.FontSize = Math.Max(1, size);
        return base.MeasureOverride(constraint);
    }

    private static void OnSymbolChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((ProfileIcon)d).UpdateSymbol();

    private void UpdateSymbol()
    {
        SymbolRegular? symbol = ProfileIconRenderer.SymbolFor(IconKey);
        _symbol.Visibility = symbol is null ? Visibility.Hidden : Visibility.Visible;
        _symbol.Symbol = symbol ?? SymbolRegular.Empty;
        _symbol.Filled = IsFilled;
    }
}

/// <summary>Maps profile symbol keys to Fluent System Icons and renders them for the tray.</summary>
public static class ProfileIconRenderer
{
    /// <summary>Chosen by the user on 2026-09-13.</summary>
    public static SymbolRegular? SymbolFor(string? key) => ProfileIcons.Normalize(key) switch
    {
        ProfileIcons.Desk => SymbolRegular.Desktop24,
        ProfileIcons.Rig => SymbolRegular.TopSpeed24,
        ProfileIcons.Vr => SymbolRegular.HeadsetVr24,
        ProfileIcons.Tv => SymbolRegular.Tv24,
        ProfileIcons.Stream => SymbolRegular.Live24,
        _ => null,
    };

    /// <summary>
    /// The symbol as a square bitmap of <paramref name="pixels"/> physical pixels, or <c>null</c> without a known symbol.
    /// Tray icons are filled because outlines are too thin at 16 px – except the VR headset, whose filled form turns
    /// into a blob at tray sizes.
    /// </summary>
    public static BitmapSource? Render(string? key, int pixels, Color color)
    {
        if (SymbolFor(key) is not { } symbol)
        {
            return null;
        }

        bool filled = symbol != SymbolRegular.HeadsetVr24;

        var host = new Grid { Width = pixels, Height = pixels };
        TextOptions.SetTextFormattingMode(host, TextFormattingMode.Display);
        host.Children.Add(new SymbolIcon
        {
            Symbol = symbol,
            Filled = filled,
            FontSize = pixels,
            Foreground = new SolidColorBrush(color),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        host.Measure(new Size(pixels, pixels));
        host.Arrange(new Rect(0, 0, pixels, pixels));
        host.UpdateLayout();

        var bitmap = new RenderTargetBitmap(pixels, pixels, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(host);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Wraps a bitmap as a single-frame icon (PNG frame, supported since Windows Vista).</summary>
    public static System.Drawing.Icon ToIcon(BitmapSource bitmap)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var png = new MemoryStream();
        encoder.Save(png);

        using var ico = new MemoryStream();
        using (var writer = new BinaryWriter(ico, Encoding.UTF8, leaveOpen: true))
        {
            // ICONDIR
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)1);
            // ICONDIRENTRY: 0 means 256 px.
            writer.Write((byte)(bitmap.PixelWidth >= 256 ? 0 : bitmap.PixelWidth));
            writer.Write((byte)(bitmap.PixelHeight >= 256 ? 0 : bitmap.PixelHeight));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write((int)png.Length);
            writer.Write(22);
            writer.Write(png.ToArray());
        }

        ico.Position = 0;
        return new System.Drawing.Icon(ico);
    }
}
