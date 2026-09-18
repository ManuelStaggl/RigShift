using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RigShift.App.Controls;
using RigShift.App.ViewModels;

namespace RigShift.App.Views;

public partial class TrayPopupView : UserControl
{
    /// <summary>Share of the work area the profile list may take before it scrolls (analysis finding I-05).</summary>
    private const double ProfileListShare = 0.6;

    private readonly TrayPopupViewModel _viewModel;

    public TrayPopupView(TrayPopupViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        // The work area changes with the taskbar and the primary display, so it is read each time the popup shows.
        IsVisibleChanged += (_, e) =>
        {
            ProfileList.MaxHeight = SystemParameters.WorkArea.Height * ProfileListShare;
            if (e.NewValue is true)
            {
                // Rises from the taskbar, like the Windows flyouts.
                Motion.PlayEnter(Flyout, 12);
            }
        };
        ProfileList.MaxHeight = SystemParameters.WorkArea.Height * ProfileListShare;
    }

    /// <summary>Digits 1–9 switch to the profile at that place.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);
        int number = e.Key switch
        {
            >= Key.D1 and <= Key.D9 => e.Key - Key.D0,
            >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad0,
            _ => 0,
        };
        if (number > 0 && Keyboard.Modifiers == ModifierKeys.None && _viewModel.SwitchToNumber(number))
        {
            e.Handled = true;
        }
    }

    private void OnMoreClick(object sender, RoutedEventArgs e) => MenuButton.Open((FrameworkElement)sender, _viewModel, alignRight: true);
}
