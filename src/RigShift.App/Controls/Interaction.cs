using System.Windows;
using System.Windows.Media;

namespace RigShift.App.Controls;

/// <summary>
/// Attached properties the RigShift control templates read: one button template serves every rank (primary,
/// secondary, ghost, icon) because the hover and pressed fills come from here instead of separate templates; a
/// field marks itself invalid without going through WPF validation.
/// </summary>
public static class Interaction
{
    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Interaction), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(Interaction), new FrameworkPropertyMetadata(null));

    public static readonly DependencyProperty HoverForegroundProperty = DependencyProperty.RegisterAttached(
        "HoverForeground", typeof(Brush), typeof(Interaction), new FrameworkPropertyMetadata(null));

    /// <summary>Fill of a disabled button; the accent and the frame are dropped so it no longer looks clickable.</summary>
    public static readonly DependencyProperty DisabledBackgroundProperty = DependencyProperty.RegisterAttached(
        "DisabledBackground", typeof(Brush), typeof(Interaction), new FrameworkPropertyMetadata(null));

    public static Brush? GetDisabledBackground(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Brush?)element.GetValue(DisabledBackgroundProperty);
    }

    public static void SetDisabledBackground(DependencyObject element, Brush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(DisabledBackgroundProperty, value);
    }

    /// <summary>The field's content failed validation: error border and, below it, a caption with the reason.</summary>
    public static readonly DependencyProperty IsInvalidProperty = DependencyProperty.RegisterAttached(
        "IsInvalid", typeof(bool), typeof(Interaction), new FrameworkPropertyMetadata(false));

    /// <summary>Corner radius of a row template: 0 for list rows, 6 for navigation entries.</summary>
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Interaction), new FrameworkPropertyMetadata(default(CornerRadius)));

    public static CornerRadius GetCornerRadius(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (CornerRadius)element.GetValue(CornerRadiusProperty);
    }

    public static void SetCornerRadius(DependencyObject element, CornerRadius value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(CornerRadiusProperty, value);
    }

    public static Brush? GetHoverBackground(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Brush?)element.GetValue(HoverBackgroundProperty);
    }

    public static void SetHoverBackground(DependencyObject element, Brush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverBackgroundProperty, value);
    }

    public static Brush? GetPressedBackground(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Brush?)element.GetValue(PressedBackgroundProperty);
    }

    public static void SetPressedBackground(DependencyObject element, Brush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(PressedBackgroundProperty, value);
    }

    public static Brush? GetHoverForeground(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Brush?)element.GetValue(HoverForegroundProperty);
    }

    public static void SetHoverForeground(DependencyObject element, Brush? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(HoverForegroundProperty, value);
    }

    public static bool GetIsInvalid(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(IsInvalidProperty);
    }

    public static void SetIsInvalid(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IsInvalidProperty, value);
    }
}
