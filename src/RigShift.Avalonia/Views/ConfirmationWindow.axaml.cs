using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Windows.Input;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Views;

/// <summary>
/// "Keep this layout?" with a countdown, centered on the new primary display. Only Enter/click or a button on a wheel,
/// button box or controller keeps the new layout; mouse movement proves nothing. Esc is registered globally for the
/// countdown, so the switch can be reverted even if this window landed on a screen without a picture.
/// </summary>
public sealed partial class ConfirmationWindow : Window
{
    private const int HotkeyId = 0x5253;

    private readonly TaskCompletionSource<ConfirmationResult> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenRegistration _cancellation;
    private readonly Guid _profileId;
    private readonly int _total;
    private int _remaining;
    private nint _hwnd;
    private ControllerButtons? _buttons;
    private bool _closing;

    // One delegate instance, so the hook can be removed again.
    private readonly Win32Properties.CustomWndProcHookCallback _hook;

    // UI thread only.
    private static ConfirmationWindow? s_open;

    public ConfirmationWindow()
    {
        InitializeComponent();
        TextScale.Apply(this);
        DialogChrome.Apply(this);
        _hook = WndProc;
    }

    private ConfirmationWindow(ConfirmationView view, TimeSpan timeout, CancellationToken cancellationToken)
        : this()
    {
        _hook = WndProc;
        BeforeTopology.Displays = view.BeforeTopology;
        BeforeName.Text = view.BeforeName;
        AfterTopology.Displays = view.AfterTopology;
        AfterName.Text = view.AfterName;
        _profileId = view.ProfileId;
        _total = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        _remaining = _total;
        UpdateCountdown();

        _timer.Tick += (_, _) => OnTick();
        Opened += (_, _) => OnOpened();
        _cancellation = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => Finish(ConfirmationResult.Cancelled)));
    }

    public static Task<ConfirmationResult> ShowAsync(ConfirmationView view, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var window = new ConfirmationWindow(view, timeout, cancellationToken);
        s_open = window;
        window.Show();
        // Started here as well: a window that never opens after the display change – a driver reset – must still take
        // the switch back.
        window._timer.Start();
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

    /// <summary>Ends the open countdown as timed out; for the deadline that answers when the window never did.</summary>
    public static void CloseOpen() => s_open?.Finish(ConfirmationResult.TimedOut);

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Alt+F4 or any other close without an answer counts as "revert".
        _closing = true;
        Finish(ConfirmationResult.Rejected);
        base.OnClosing(e);
    }

    private void OnOpened()
    {
        if (TryGetPlatformHandle()?.Handle is { } hwnd)
        {
            _hwnd = hwnd;
            Win32Properties.AddWndProcHookCallback(this, _hook);
            if (!NativeWindow.RegisterEscapeHotkey(hwnd, HotkeyId))
            {
                Log.Warning("Global Esc hotkey for the confirmation window could not be registered; Esc works only while the window has focus");
            }

            // At the rig there may be no keyboard, only the wheel.
            _buttons = ControllerButtons.Listen(hwnd, Log.Logger);
            NativeWindow.CenterOnPrimaryMonitor(hwnd);
        }

        Activate();
        KeepButton.Focus();
    }

    private void OnTick()
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

    private void OnKeep(object? sender, RoutedEventArgs e) => Finish(ConfirmationResult.Confirmed);

    private void OnRevert(object? sender, RoutedEventArgs e) => Finish(ConfirmationResult.Rejected);

    private nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmHotkey && wParam == HotkeyId)
        {
            handled = true;
            Finish(ConfirmationResult.Rejected);
        }
        else if (msg == ControllerButtons.WmInput && _buttons is { } buttons && buttons.IsButtonChange(lParam))
        {
            // Not handled: DefWindowProc frees the input. The window closes after this message, not inside it.
            _buttons = null;
            buttons.Dispose();
            Log.Information("A button on a wheel, button box or controller confirmed the switch");
            Dispatcher.UIThread.Post(() => Finish(ConfirmationResult.Confirmed));
        }

        return 0;
    }

    private void UpdateCountdown()
    {
        Ring.Maximum = _total;
        Ring.Value = _remaining;
        Seconds.Text = _remaining.ToString(Loc.Instance.Culture);
        HintText.Text = Loc.Format("Confirm_Hint", _remaining);
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
            Win32Properties.RemoveWndProcHookCallback(this, _hook);
        }

        _buttons?.Dispose();
        if (!_closing)
        {
            Close();
        }
    }
}
