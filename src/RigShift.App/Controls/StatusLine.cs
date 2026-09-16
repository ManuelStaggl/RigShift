using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RigShift.App.Controls;

/// <summary>Color of a status: always paired with text, never the only signal.</summary>
public enum StatusKind
{
    Neutral,
    Ok,
    Warn,
    Error,

    /// <summary>Something is in progress (a switch, a running game).</summary>
    Accent,
}

/// <summary>An 8 px dot and one caption line: "Ready · Dash missing (optional)". The text is a result key, never rephrased.</summary>
public sealed class StatusLine : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(StatusKind), typeof(StatusLine), new PropertyMetadata(StatusKind.Neutral));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(StatusLine), new PropertyMetadata(string.Empty));

    /// <summary>Set by the style from <see cref="Kind"/>.</summary>
    public static readonly DependencyProperty DotBrushProperty = DependencyProperty.Register(
        nameof(DotBrush), typeof(Brush), typeof(StatusLine), new PropertyMetadata(null));

    static StatusLine()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(StatusLine), new FrameworkPropertyMetadata(typeof(StatusLine)));
    }

    public StatusKind Kind
    {
        get => (StatusKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public Brush? DotBrush
    {
        get => (Brush?)GetValue(DotBrushProperty);
        set => SetValue(DotBrushProperty, value);
    }
}
