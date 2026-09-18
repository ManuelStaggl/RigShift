using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using RigShift.App.Localization;

namespace RigShift.App.Controls;

/// <summary>
/// The countdown of the confirmation window: a ring that empties clockwise with the seconds in its middle.
/// The number carries the information; the ring only makes the time visible at a glance.
/// </summary>
public sealed class CountdownRing : ContentControl
{
    /// <summary>Radius, thickness and font of the 72 × 72 ring (spec 7.3, mockup Bestaetigung).</summary>
    private const double Radius = 31;
    private const double Thickness = 4;

    /// <summary>WPF measures a dash pattern in multiples of the stroke thickness, not in pixels.</summary>
    private static readonly double Dashes = 2 * Math.PI * Radius / Thickness;

    private readonly Ellipse _arc;
    private readonly TextBlock _text;

    public CountdownRing()
    {
        Focusable = false;
        IsTabStop = false;

        var track = new Ellipse { Width = Radius * 2, Height = Radius * 2, StrokeThickness = Thickness };
        track.SetResourceReference(Shape.StrokeProperty, "RigShift.Brush.Line");

        // Starts at the top and runs clockwise, like every other countdown ring.
        _arc = new Ellipse
        {
            Width = Radius * 2,
            Height = Radius * 2,
            StrokeThickness = Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(-90),
        };
        _arc.SetResourceReference(Shape.StrokeProperty, "RigShift.Brush.Accent");

        _text = new TextBlock
        {
            FontSize = 22,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _text.SetResourceReference(TextBlock.FontFamilyProperty, "RigShift.Font.Text");
        _text.SetResourceReference(TextBlock.ForegroundProperty, "RigShift.Brush.TextPrimary");

        var root = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        root.Children.Add(track);
        root.Children.Add(_arc);
        root.Children.Add(_text);
        Content = root;
        SetRemaining(0, 1);
    }

    /// <summary>Shows <paramref name="remaining"/> seconds of <paramref name="total"/>.</summary>
    public void SetRemaining(int remaining, int total)
    {
        _text.Text = remaining.ToString(Loc.Instance.Culture);
        double fraction = total > 0 ? Math.Clamp((double)remaining / total, 0, 1) : 0;

        // A zero-length dash with a round cap would still draw a dot; leave the ring empty instead.
        _arc.StrokeDashArray = fraction <= 0
            ? [0, Dashes]
            : [Dashes * fraction, Dashes];
        _arc.Visibility = fraction <= 0 ? Visibility.Hidden : Visibility.Visible;
    }
}
