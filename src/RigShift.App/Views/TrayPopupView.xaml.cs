using System.Windows;
using System.Windows.Controls;
using RigShift.App.ViewModels;

namespace RigShift.App.Views;

public partial class TrayPopupView : UserControl
{
    /// <summary>Share of the work area the profile list may take before it scrolls (analysis finding I-05).</summary>
    private const double ProfileListShare = 0.6;

    public TrayPopupView(TrayPopupViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        // The work area changes with the taskbar and the primary display, so it is read each time the popup shows.
        IsVisibleChanged += (_, _) => ProfileList.MaxHeight = SystemParameters.WorkArea.Height * ProfileListShare;
        ProfileList.MaxHeight = SystemParameters.WorkArea.Height * ProfileListShare;
    }
}
