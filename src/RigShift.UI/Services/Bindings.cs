using Microsoft.UI.Xaml;

namespace RigShift.UI.Services;

/// <summary>Small functions for <c>x:Bind</c> that XAML cannot say itself.</summary>
public static class Bindings
{
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static bool IsSet(object? value) => value is not null;

    /// <summary>The value itself: lets a two-way binding take a BindBack that ignores a passing null.</summary>
    public static object? Same(object? value) => value;

    /// <summary>A <c>double?</c> for a NumberBox, where empty is NaN.</summary>
    public static double Number(double? value) => value ?? double.NaN;

    /// <summary>What a NumberBox holds, back as <c>double?</c>: empty (NaN) is null.</summary>
    public static double? FromNumber(double value) => double.IsNaN(value) ? null : value;

    public static Visibility CollapsedIfSet(object? value) => value is null ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility VisibleIfText(string? value) => string.IsNullOrEmpty(value) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>One of two theme brushes by a flag, e.g. the selected surface for the active profile.</summary>
    public static Microsoft.UI.Xaml.Media.Brush BrushIf(bool value, string whenTrue, string whenFalse) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[value ? whenTrue : whenFalse];
}
