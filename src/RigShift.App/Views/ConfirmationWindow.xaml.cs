using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Threading;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>
/// "Keep these display settings?" with a countdown, centered on the new primary display.
/// Only Enter/click keeps the new topology; mouse movement proves nothing. Esc is registered globally for the
/// countdown, so the switch can be reverted even if this window landed on a screen without a picture.
/// </summary>
public partial class ConfirmationWindow : FluentWindow
{
    private const int HotkeyId = 0x5253;

    private readonly TaskCompletionSource<ConfirmationResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenRegistration _cancellation;
    private readonly Guid _profileId;

    /// <summary>The countdown on screen, if any. UI thread only.</summary>
    private static ConfirmationWindow? s_open;

    private HwndSource? _source;
    private nint _hwnd;
    private int _remaining;
    private bool _closing;

    private ConfirmationWindow(Profile profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        InitializeComponent();
        ProfileText.Text = profile.Name;
        _profileId = profile.Id;
        _remaining = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        UpdateCountdown();

        _timer.Tick += OnTick;
        SourceInitialized += OnSourceInitialized;
        ContentRendered += OnContentRendered;
        _cancellation = cancellationToken.Register(() => Dispatcher.InvokeAsync(() => Finish(ConfirmationResult.TimedOut)));
    }

    public static Task<ConfirmationResult> ShowAsync(Profile profile, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var window = new ConfirmationWindow(profile, timeout, cancellationToken);
        s_open = window;
        window.Show();
        return window._result.Task;
    }

    /// <summary>Confirms the open countdown if it belongs to <paramref name="profileId"/> (its hotkey was pressed again).</summary>
    public static bool TryConfirm(Guid profileId)
    {
        if (s_open is not { } window || window._profileId != profileId)
        {
            return false;
        }

        window.Finish(ConfirmationResult.Confirmed);
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 or any other close without an answer counts as "revert".
        _closing = true;
        Finish(ConfirmationResult.Rejected);
        base.OnClosing(e);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        if (!NativeWindow.RegisterEscapeHotkey(_hwnd, HotkeyId))
        {
            Log.Warning("Global Esc hotkey for the confirmation window could not be registered; Esc works only while the window has focus");
        }

        SystemThemeWatcher.Watch(this, WindowBackdropType.Mica, updateAccents: false);
    }

    private void OnContentRendered(object? sender, EventArgs e)
    {
        var content = (FrameworkElement)Content;
        Log.Debug(
            "Confirmation window {Width}x{Height} (min {MinWidth}x{MinHeight}), content {ContentWidth}x{ContentHeight}",
            ActualWidth, ActualHeight, MinWidth, MinHeight, content.ActualWidth, content.ActualHeight);
        NativeWindow.CenterOnPrimaryMonitor(_hwnd);
        Activate();
        KeepButton.Focus();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining <= 0)
        {
            Finish(ConfirmationResult.TimedOut);
        }
        else
        {
            UpdateCountdown();
        }
    }

    private void OnKeep(object sender, RoutedEventArgs e) => Finish(ConfirmationResult.Confirmed);

    private void OnRevert(object sender, RoutedEventArgs e) => Finish(ConfirmationResult.Rejected);

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Finish(ConfirmationResult.Rejected);
        }

        return 0;
    }

    private void UpdateCountdown()
    {
        CountdownText.Text = Loc.Format("Confirm_Countdown", _remaining);
        UIElementAutomationPeer.CreatePeerForElement(CountdownText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void Finish(ConfirmationResult result)
    {
        if (!_result.TrySetResult(result))
        {
            return;
        }

        Log.Information("Confirmation finished: {Result}", result);
        if (s_open == this)
        {
            s_open = null;
        }

        _timer.Stop();
        _cancellation.Dispose();
        if (_hwnd != 0)
        {
            NativeWindow.UnregisterHotkey(_hwnd, HotkeyId);
        }

        _source?.RemoveHook(WndProc);
        if (!_closing)
        {
            Close();
        }
    }
}
