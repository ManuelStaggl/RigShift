using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace RigShift.App.Controls;

/// <summary>
/// Motion tokens. Templates take them with <c>{x:Static controls:Motion.Fast}</c>; code uses the same values. When
/// Windows has "Animation effects" off (<see cref="SystemParameters.ClientAreaAnimation"/>), every duration is zero,
/// so each state is reached at once. Nothing runs longer than 250 ms and nothing blocks input.
/// </summary>
public static class Motion
{
    /// <summary>Whether animations run at all; read once at start like the durations below.</summary>
    public static bool Enabled { get; } = SystemParameters.ClientAreaAnimation;

    /// <summary>100 ms: hover and pressed fills of buttons, rows, tabs, chips.</summary>
    public static Duration Fast { get; } = Of(100);

    /// <summary>180 ms: selection indicators, tab underline, segment pill, chevrons, check marks.</summary>
    public static Duration Base { get; } = Of(180);

    /// <summary>220 ms: pages, tab content, dialogs, menus and the save bar coming in.</summary>
    public static Duration Enter { get; } = Of(220);

    /// <summary>120 ms: the same elements leaving.</summary>
    public static Duration Exit { get; } = Of(120);

    /// <summary>250 ms: layout changes such as the topology moving to a new arrangement.</summary>
    public static Duration Layout { get; } = Of(250);

    /// <summary>Standard curve for state changes (close to cubic-bezier 0.2, 0, 0, 1).</summary>
    public static IEasingFunction Standard { get; } = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Decelerate: things arriving.</summary>
    public static IEasingFunction Decelerate { get; } = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });

    /// <summary>Accelerate: things leaving.</summary>
    public static IEasingFunction Accelerate { get; } = Freeze(new CubicEase { EasingMode = EasingMode.EaseIn });

    /// <summary>
    /// On a TabControl: the content of a newly chosen tab fades in and rises 8 px. Only the control's own selection
    /// counts – a combo box inside a tab raises the same routed event.
    /// </summary>
    public static readonly DependencyProperty AnimateTabContentProperty = DependencyProperty.RegisterAttached(
        "AnimateTabContent", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnAnimateTabContentChanged));

    public static bool GetAnimateTabContent(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(AnimateTabContentProperty);
    }

    public static void SetAnimateTabContent(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(AnimateTabContentProperty, value);
    }

    /// <summary>Entrance: fade in and rise by <paramref name="offset"/> px (negative = drop in from above).</summary>
    public static void PlayEnter(UIElement element, double offset)
    {
        ArgumentNullException.ThrowIfNull(element);
        if (!Enabled)
        {
            return;
        }

        var shift = new TranslateTransform(0, offset);
        element.RenderTransform = shift;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Enter) { EasingFunction = Decelerate });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(offset, 0, Enter) { EasingFunction = Decelerate });
    }

    private static void OnAnimateTabContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TabControl tabs)
        {
            return;
        }

        tabs.SelectionChanged -= OnTabSelectionChanged;
        if ((bool)e.NewValue)
        {
            tabs.SelectionChanged += OnTabSelectionChanged;
        }
    }

    private static void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, sender) || sender is not TabControl { IsLoaded: true } tabs
            || tabs.Template?.FindName("PART_SelectedContentHost", tabs) is not UIElement host)
        {
            return;
        }

        PlayEnter(host, 8);
    }

    private static Duration Of(int milliseconds) =>
        new(Enabled ? TimeSpan.FromMilliseconds(milliseconds) : TimeSpan.Zero);

    private static EasingFunctionBase Freeze(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }
}
