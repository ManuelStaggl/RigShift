using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Views;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Tray;

/// <summary>
/// The popup next to the tray icon. No title bar, not in Alt+Tab, closes when it loses the focus or on Esc. It is placed
/// once layout has measured its height, and stays invisible until then, so it never flashes elsewhere.
/// </summary>
public sealed partial class TrayPopupWindow : Window
{
    private bool _hadFocus;

    public TrayPopupWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
    }

    public TrayPopupWindow(TrayPopupViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        Opened += (_, _) => Dispatcher.UIThread.Post(PlaceAndShow, DispatcherPriority.Loaded);
        // Closes on the first focus loss after it had the focus: one that never got it would close at once.
        Activated += (_, _) => _hadFocus = true;
        Deactivated += (_, _) =>
        {
            if (_hadFocus)
            {
                Close();
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
    }

    private TrayPopupViewModel ViewModel => (TrayPopupViewModel)DataContext!;

    /// <summary>Next to the cursor – at the tray icon that was clicked – on whichever taskbar edge and monitor that is.</summary>
    private void PlaceAndShow()
    {
        (int X, int Y)? placed = TryGetPlatformHandle()?.Handle is { } handle ? NativeWindow.PlaceNearCursor(handle) : null;
        Opacity = 1;
        Activate();
        Log.Information("Tray popup opened at {Position}", placed?.ToString() ?? "default position");
    }

    private void OnProfileClick(object? sender, RoutedEventArgs e) => ViewModel.SwitchCommand.Execute(((Control)sender!).Tag as ProfileItem);

    private void OnGameClick(object? sender, RoutedEventArgs e) => ViewModel.PlayCommand.Execute(((Control)sender!).Tag as GameItem);
}
