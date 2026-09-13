using System.Windows.Controls;
using RigShift.App.ViewModels;

namespace RigShift.App.Views;

public partial class TrayPopupView : UserControl
{
    public TrayPopupView(TrayPopupViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
