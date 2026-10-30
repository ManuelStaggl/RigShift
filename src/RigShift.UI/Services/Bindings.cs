using Microsoft.UI.Xaml;

namespace RigShift.UI.Services;

/// <summary>Small functions for <c>x:Bind</c> that XAML cannot say itself.</summary>
public static class Bindings
{
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>One of two theme brushes by a flag, e.g. the selected surface for the active profile.</summary>
    public static Microsoft.UI.Xaml.Media.Brush BrushIf(bool value, string whenTrue, string whenFalse) =>
        (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[value ? whenTrue : whenFalse];
}
