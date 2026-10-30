using Microsoft.UI.Xaml;

namespace RigShift.UI.Themes;

/// <summary>Data templates used from code (dialogs built without XAML); a class so they can use <c>x:Bind</c>.</summary>
public sealed partial class Templates : ResourceDictionary
{
    public Templates()
    {
        InitializeComponent();
    }
}
