using System.Windows;
using System.Windows.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// Lays its children out as columns while they all fit, and stacks them under each other when they do not.
/// A horizontal <see cref="StackPanel"/> or a grid of fixed columns clips its content at the window's minimum
/// width; this panel keeps every column readable instead.
/// </summary>
/// <remarks>
/// A child with an explicit <see cref="FrameworkElement.Width"/> keeps that width. A child with
/// <see cref="MinColumnWidthProperty"/> is flexible: it never falls below that width and shares the space left
/// over with the other flexible children. A child with neither takes the width it asks for and does not grow.
/// </remarks>
public sealed class AdaptiveColumns : Panel
{
    /// <summary>Space between two columns, and between two rows once the columns are stacked.</summary>
    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap),
        typeof(double),
        typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(24d, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Width a flexible child may not fall below; below it the panel stacks.</summary>
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.RegisterAttached(
        "MinColumnWidth",
        typeof(double),
        typeof(AdaptiveColumns),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    private bool _stacked;
    private double _extra;

    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public static double GetMinColumnWidth(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(MinColumnWidthProperty);
    }

    public static void SetMinColumnWidth(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(MinColumnWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double gap = Gap;
        double needed = 0;
        int visible = 0;
        int flexible = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            visible++;
            if (IsFlexible(child))
            {
                flexible++;
                needed += GetMinColumnWidth(child);
            }
            else
            {
                // Fixed or content sized: ask once, unbounded, and take that as the column's width.
                child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                needed += Fixed(child);
            }
        }

        if (visible == 0)
        {
            return new Size(0, 0);
        }

        double gaps = gap * (visible - 1);
        needed += gaps;
        _stacked = !double.IsInfinity(availableSize.Width) && needed > availableSize.Width + 0.5;
        _extra = !_stacked && flexible > 0 && !double.IsInfinity(availableSize.Width)
            ? Math.Max(0, (availableSize.Width - needed) / flexible)
            : 0;

        double width = 0;
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            child.Measure(new Size(Slot(child, availableSize.Width), double.PositiveInfinity));
            if (_stacked)
            {
                width = Math.Max(width, child.DesiredSize.Width);
                height += child.DesiredSize.Height;
            }
            else
            {
                width += Slot(child, availableSize.Width);
                height = Math.Max(height, child.DesiredSize.Height);
            }
        }

        return _stacked ? new Size(width, height + gaps) : new Size(width + gaps, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double gap = Gap;
        double x = 0;
        double y = 0;

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                continue;
            }

            if (_stacked)
            {
                // Stacked columns start at the left edge; an explicit width would otherwise centre the child.
                double width = Math.Min(child.DesiredSize.Width, finalSize.Width);
                if (IsFlexible(child))
                {
                    width = finalSize.Width;
                }

                child.Arrange(new Rect(0, y, width, child.DesiredSize.Height));
                y += child.DesiredSize.Height + gap;
            }
            else
            {
                double slot = Slot(child, finalSize.Width);
                child.Arrange(new Rect(x, 0, slot, finalSize.Height));
                x += slot + gap;
            }
        }

        return finalSize;
    }

    private static bool IsFlexible(UIElement child) => GetMinColumnWidth(child) > 0;

    private static double Fixed(UIElement child)
    {
        if (child is FrameworkElement element && !double.IsNaN(element.Width))
        {
            return element.Width;
        }

        return child.DesiredSize.Width;
    }

    private double Slot(UIElement child, double available)
    {
        if (_stacked)
        {
            return available;
        }

        return IsFlexible(child) ? GetMinColumnWidth(child) + _extra : Fixed(child);
    }
}
