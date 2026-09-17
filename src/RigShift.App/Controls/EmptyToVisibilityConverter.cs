using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RigShift.App.Controls;

/// <summary>Shows what belongs to an empty text field – the hint behind a name field, and nothing once it is filled.</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
