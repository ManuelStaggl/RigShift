using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RigShift.App.Controls;

/// <summary><c>true</c> hides, <c>false</c> shows – the counterpart of <see cref="BooleanToVisibilityConverter"/>.</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility.Collapsed;
}
