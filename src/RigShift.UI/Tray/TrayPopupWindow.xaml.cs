using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using RigShift.App.ViewModels;
using RigShift.Windows.Ui;
using Serilog;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;

namespace RigShift.UI.Tray;

/// <summary>
/// The popup next to the tray icon: profiles to switch to, games to start, open and exit. No title bar, not in Alt+Tab,
/// closes when it loses the focus or on Esc.
/// </summary>
public sealed partial class TrayPopupWindow : Window
{
    /// <summary>Device-independent width; the height follows the content.</summary>
    private const double PopupWidth = 340;

    public TrayPopupWindow(TrayPopupViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        AppWindow.IsShownInSwitchers = false;
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        // Closes on the first focus loss after it had the focus: one that never got it (opened from the background)
        // would close at once.
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _hadFocus = true;
            }
            else if (_hadFocus)
            {
                Log.Debug("Tray popup lost the focus, closing");
                Close();
            }
        };
    }

    private bool _hadFocus;

    public TrayPopupViewModel ViewModel { get; }

    /// <summary>Sizes the window to its content, puts it next to the cursor – at the tray icon that was clicked – and shows it.</summary>
    public void ShowNearCursor()
    {
        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = NativeWindow.DpiScale(handle);
        Root.Measure(new Size(PopupWidth, double.PositiveInfinity));
        AppWindow.Resize(new SizeInt32((int)Math.Ceiling(PopupWidth * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale)));
        (int X, int Y)? placed = NativeWindow.PlaceNearCursor(handle);
        Activate();
        Log.Information("Tray popup opened at {Position}", placed?.ToString() ?? "default position");
        Root.Loaded += (_, _) => FitToContent(handle);
    }

    /// <summary>Once in the visual tree the lists know their real height; the first measure only had the frame.</summary>
    private void FitToContent(nint handle)
    {
        double scale = NativeWindow.DpiScale(handle);
        Root.Measure(new Size(PopupWidth, double.PositiveInfinity));
        AppWindow.Resize(new SizeInt32((int)Math.Ceiling(PopupWidth * scale), (int)Math.Ceiling(Root.DesiredSize.Height * scale)));
        NativeWindow.PlaceNearCursor(handle);
        ProfileList.Focus(FocusState.Programmatic);
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void OnProfileClick(object sender, ItemClickEventArgs e) => ViewModel.SwitchCommand.Execute(e.ClickedItem as ProfileItem);

    private void OnGameClick(object sender, ItemClickEventArgs e) => ViewModel.PlayCommand.Execute(e.ClickedItem as GameItem);
}
