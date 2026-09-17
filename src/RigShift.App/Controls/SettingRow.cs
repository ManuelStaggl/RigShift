using System.Windows;
using System.Windows.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// One line of the settings page: title, an optional explaining caption, and the control that changes the
/// setting on the right. The content is the control, so the row carries no state of its own.
/// </summary>
public sealed class SettingRow : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
        nameof(Caption), typeof(string), typeof(SettingRow), new PropertyMetadata(string.Empty));

    static SettingRow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SettingRow), new FrameworkPropertyMetadata(typeof(SettingRow)));
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Empty for a setting that explains itself; the line is then hidden instead of left blank.</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }
}
