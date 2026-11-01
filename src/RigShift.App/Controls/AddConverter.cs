using System.Globalization;
using System.Windows.Data;

namespace RigShift.App.Controls;

/// <summary>Adds <see cref="Amount"/> to a double – a popup's width plus the margin its shadow falls into.</summary>
public sealed class AddConverter : IValueConverter
{
    public double Amount { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d + Amount : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double d ? d - Amount : value;
}
