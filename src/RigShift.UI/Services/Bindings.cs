using Microsoft.UI.Xaml;

namespace RigShift.UI.Services;

/// <summary>Small functions for <c>x:Bind</c> that XAML cannot say itself.</summary>
public static class Bindings
{
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
