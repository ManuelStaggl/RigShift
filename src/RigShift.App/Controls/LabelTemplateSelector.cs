using System.Windows;
using System.Windows.Controls;

namespace RigShift.App.Controls;

/// <summary>
/// Puts text content of a check box or radio button in the wrapping label and shows element content as it is. A
/// template set straight in the style would turn a panel into its type name ("System.Windows.Controls.StackPanel").
/// </summary>
public sealed class LabelTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is UIElement ? null : TextTemplate;
}
