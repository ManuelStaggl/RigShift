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
/// The quick switcher: in the middle of the display with the mouse, focused on the search box. Like the tray popup it
/// has no title bar, stays out of Alt+Tab and closes when it loses the focus.
/// </summary>
public sealed partial class QuickSwitchWindow : Window
{
    private static QuickSwitchWindow? _open;
    private bool _hadFocus;

    public QuickSwitchWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
    }

    private QuickSwitchWindow(QuickSwitchViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
        viewModel.Done += OnDone;
        Opened += (_, _) => Dispatcher.UIThread.Post(PlaceAndShow, DispatcherPriority.Loaded);
        Activated += (_, _) => _hadFocus = true;
        Deactivated += (_, _) =>
        {
            if (_hadFocus)
            {
                Close();
            }
        };

        // Tunnelling: the search box would take Up, Down and Enter for itself.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        Closed += (_, _) =>
        {
            viewModel.Done -= OnDone;
            _open = null;
        };
    }

    private QuickSwitchViewModel ViewModel => (QuickSwitchViewModel)DataContext!;

    /// <summary>Opens the switcher, or brings it back to the front when the hotkey is pressed while it is open.</summary>
    public static void Open(QuickSwitchViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        if (_open is { } open)
        {
            open.Activate();
            return;
        }

        viewModel.Refresh();
        _open = new QuickSwitchWindow(viewModel);
        _open.Show();
    }

    private void PlaceAndShow()
    {
        if (TryGetPlatformHandle()?.Handle is { } handle)
        {
            NativeWindow.OpenOnCursorMonitor(handle, Bounds.Width, Bounds.Height);
        }

        Opacity = 1;
        Activate();
        Search.Focus();
        Log.Information("Quick switcher opened with {Count} entries", ViewModel.Items.Count);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Down:
                ViewModel.Move(1);
                break;
            case Key.Up:
                ViewModel.Move(-1);
                break;
            case Key.Enter:
                ViewModel.Run();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void OnTapped(object? sender, TappedEventArgs e) => ViewModel.Run();

    private void OnDone(object? sender, EventArgs e) => Close();
}
