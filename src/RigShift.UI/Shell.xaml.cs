using Microsoft.UI.Xaml.Controls;

namespace RigShift.UI;

/// <summary>Navigation and page frame inside the main window; built again when the language changes.</summary>
public sealed partial class Shell : UserControl
{
    public Shell()
    {
        InitializeComponent();
    }
}
