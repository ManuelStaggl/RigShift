using System.Windows.Data;
using System.Windows.Markup;

namespace RigShift.App.Localization;

/// <summary><c>Text="{loc:Tr Nav_Profiles}"</c> – a live binding to <see cref="Loc"/>.</summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key)
    {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding("[" + Key + "]") { Source = Loc.Instance, Mode = BindingMode.OneWay }.ProvideValue(serviceProvider);
}
