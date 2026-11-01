using System.Windows;
using System.Windows.Media;

namespace RigShift.App.Controls;

/// <summary>
/// One display drawn as a device: dark bezel, screen with a soft vertical gradient and a gloss line at the top, and –
/// in the large sizes – a stand below its bounds. Optional displays are only a dashed outline; missing ones get a grey
/// screen with a faint hatch. Colors come from the tokens, so high contrast swaps them.
/// </summary>
internal sealed class DisplayShape : FrameworkElement
{
    public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.Register(
        nameof(IsHovered), typeof(bool), typeof(DisplayShape), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(DisplayShape), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));

    public DisplayShape()
    {
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    public double Radius { get; init; } = 2;

    public double Bezel { get; init; } = 1.5;

    /// <summary>Height of the stand under the bounds; 0 draws none.</summary>
    public double StandHeight { get; init; }

    public bool IsOptional { get; init; }

    public bool IsOff { get; init; }

    public bool IsMissing { get; init; }

    public bool ShowGloss { get; init; }

    public bool IsHovered
    {
        get => (bool)GetValue(IsHoveredProperty);
        set => SetValue(IsHoveredProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ArgumentNullException.ThrowIfNull(dc);
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1)
        {
            return;
        }

        var outer = new Rect(0, 0, w, h);
        if (IsOptional)
        {
            var dashed = new Pen(Brush("RigShift.Brush.LineStrong"), 1.2) { DashStyle = new DashStyle([4, 3], 0) };
            dc.DrawRoundedRectangle(Brush("RigShift.Brush.PressedOverlay"), dashed, Inset(outer, 0.6), Radius, Radius);
            return;
        }

        if (StandHeight > 0)
        {
            DrawStand(dc, w, h);
        }

        Pen edge = IsSelected
            ? new Pen(Brush("RigShift.Brush.Accent"), 2)
            : new Pen(Brush("RigShift.Brush.DisplayEdge"), 1);
        dc.DrawRoundedRectangle(Brush("RigShift.Brush.DisplayBezel"), edge, Inset(outer, edge.Thickness / 2), Radius, Radius);

        Rect screen = Inset(outer, Bezel);
        if (screen.Width < 1 || screen.Height < 1)
        {
            return;
        }

        double inner = Math.Max(Radius - (Bezel / 2), 1);
        Brush fill = IsMissing ? Brush("RigShift.Brush.DisplayScreenMissing")
            : IsOff ? Brush("RigShift.Brush.DisplayFillOff")
            : Brush("RigShift.Brush.DisplayScreen");
        dc.DrawRoundedRectangle(fill, null, screen, inner, inner);
        if (IsMissing)
        {
            dc.DrawRoundedRectangle(Brush("RigShift.Brush.Hatch"), null, screen, inner, inner);
        }

        if (IsHovered)
        {
            dc.DrawRoundedRectangle(Brush("RigShift.Brush.HoverOverlay"), null, screen, inner, inner);
        }

        if (ShowGloss && screen.Width > 2 * inner)
        {
            var gloss = new Pen(Brush("RigShift.Brush.DisplayGloss"), 1);
            dc.DrawLine(gloss, new Point(screen.Left + inner, screen.Top + 0.5), new Point(screen.Right - inner, screen.Top + 0.5));
        }
    }

    private void DrawStand(DrawingContext dc, double w, double h)
    {
        Brush stand = Brush("RigShift.Brush.DisplayStand");
        double cx = w / 2;
        double neck = Math.Max(StandHeight - 3, 2);
        double foot = Math.Min(w * 0.36, StandHeight * 6);
        dc.DrawRoundedRectangle(stand, null, new Rect(cx - 4, h - 1, 8, neck + 1), 1.5, 1.5);
        dc.DrawRoundedRectangle(stand, null, new Rect(cx - (foot / 2), h + neck - 1, foot, 3.5), 1.75, 1.75);
    }

    private static Rect Inset(Rect rect, double by) =>
        rect.Width > 2 * by && rect.Height > 2 * by ? new Rect(rect.X + by, rect.Y + by, rect.Width - (2 * by), rect.Height - (2 * by)) : rect;

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Transparent;
}
