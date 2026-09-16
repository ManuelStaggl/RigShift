using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RigShift.App.Controls;

/// <summary>
/// The 2 px indeterminate progress line shown while a switch runs: a bar of 40 % width travels across the track once
/// every 1.4 s. One of the two motions of the UI (the other is the countdown ring). Animates only while visible.
/// </summary>
public sealed class ProgressLine : FrameworkElement
{
    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(ProgressLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(ProgressLine), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 … 1, animated: where the bar is on its way across the track.</summary>
    public static readonly DependencyProperty OffsetProperty = DependencyProperty.Register(
        nameof(Offset), typeof(double), typeof(ProgressLine), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double BarShare = 0.4;

    public ProgressLine()
    {
        IsVisibleChanged += (_, _) => UpdateAnimation();
        Unloaded += (_, _) => BeginAnimation(OffsetProperty, null);
    }

    public Brush? TrackBrush
    {
        get => (Brush?)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public Brush? BarBrush
    {
        get => (Brush?)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public double Offset
    {
        get => (double)GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width, double.IsNaN(Height) ? 2 : Height);

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        drawingContext.DrawRectangle(TrackBrush, null, new Rect(0, 0, width, height));
        double barWidth = width * BarShare;
        double x = (Offset * (width + barWidth)) - barWidth;
        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height)));
        drawingContext.DrawRectangle(BarBrush, null, new Rect(x, 0, barWidth, height));
        drawingContext.Pop();
    }

    private void UpdateAnimation()
    {
        if (IsVisible)
        {
            BeginAnimation(OffsetProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        else
        {
            BeginAnimation(OffsetProperty, null);
        }
    }
}
