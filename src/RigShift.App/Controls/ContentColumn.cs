using System.Windows;
using System.Windows.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// Gives its child the full width up to <see cref="MaxContentWidth"/> and keeps it at the left edge beyond that.
/// A stretched element with <see cref="FrameworkElement.MaxWidth"/> would be centred, and a left-aligned one
/// shrinks to its content – neither suits a detail pane whose cards should fill a readable column.
/// </summary>
public sealed class ContentColumn : Decorator
{
    public static readonly DependencyProperty MaxContentWidthProperty = DependencyProperty.Register(
        nameof(MaxContentWidth),
        typeof(double),
        typeof(ContentColumn),
        new FrameworkPropertyMetadata(double.PositiveInfinity, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MaxContentWidth
    {
        get => (double)GetValue(MaxContentWidthProperty);
        set => SetValue(MaxContentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        if (Child is null)
        {
            return default;
        }

        Child.Measure(new Size(Math.Min(constraint.Width, MaxContentWidth), constraint.Height));
        return Child.DesiredSize;
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(0, 0, Math.Min(arrangeSize.Width, MaxContentWidth), arrangeSize.Height));
        return arrangeSize;
    }
}
