using Microsoft.UI.Xaml.Markup;
using RigShift.App.Localization;

namespace RigShift.UI.Localization;

/// <summary>
/// <c>{l:Tr Key=Nav_Overview}</c>: a text from the strings the view models use, in the app's language. Read when the
/// XAML loads; a language change loads the shell again.
/// </summary>
[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class Tr : MarkupExtension
{
    public string Key { get; set; } = string.Empty;

    protected override object ProvideValue() => Loc.Instance[Key];
}
