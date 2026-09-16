using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace RigShift.App.Controls;

public enum InfoKind
{
    Info,
    Warn,
    Error,
}

/// <summary>
/// A 40 px bar with a 3 px colored edge, a symbol, one line of text and an optional action on the right: planner
/// warnings, wizard checks, load errors. Not the WPF-UI InfoBar – no title, no close button, no fill in the state color.
/// </summary>
public sealed class InfoBar : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(InfoKind), typeof(InfoBar), new PropertyMetadata(InfoKind.Info));

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(InfoBar), new PropertyMetadata(string.Empty));

    /// <summary>Usually a secondary button, 28 px high; nothing when null.</summary>
    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(InfoBar), new PropertyMetadata(null));

    /// <summary>Set by the style from <see cref="Kind"/>.</summary>
    public static readonly DependencyProperty EdgeBrushProperty = DependencyProperty.Register(
        nameof(EdgeBrush), typeof(Brush), typeof(InfoBar), new PropertyMetadata(null));

    /// <summary>Set by the style from <see cref="Kind"/>.</summary>
    public static readonly DependencyProperty SymbolProperty = DependencyProperty.Register(
        nameof(Symbol), typeof(SymbolRegular), typeof(InfoBar), new PropertyMetadata(SymbolRegular.Info16));

    static InfoBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(InfoBar), new FrameworkPropertyMetadata(typeof(InfoBar)));
    }

    public InfoKind Kind
    {
        get => (InfoKind)GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    public Brush? EdgeBrush
    {
        get => (Brush?)GetValue(EdgeBrushProperty);
        set => SetValue(EdgeBrushProperty, value);
    }

    public SymbolRegular Symbol
    {
        get => (SymbolRegular)GetValue(SymbolProperty);
        set => SetValue(SymbolProperty, value);
    }
}
