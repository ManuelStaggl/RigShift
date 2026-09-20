using System.Windows.Interop;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;

namespace RigShift.App.Services;

/// <summary>Windows' side of the hotkeys, so the bookkeeping in <see cref="HotkeyService"/> runs in tests without taking real keys.</summary>
internal interface IHotkeyRegistrar : IDisposable
{
    /// <summary>The registration id of the combination that was pressed.</summary>
    event EventHandler<int>? Pressed;

    bool Register(int id, Hotkey hotkey, out int error);

    void Unregister(int id);
}

internal sealed class Win32HotkeyRegistrar : IHotkeyRegistrar
{
    private readonly HwndSource _source;

    public Win32HotkeyRegistrar()
    {
        // Message-only window: WM_HOTKEY is posted to the registering window, no broadcast needed.
        _source = new HwndSource(new HwndSourceParameters("RigShift.Hotkeys") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        _source.AddHook(WndProc);
    }

    public event EventHandler<int>? Pressed;

    public bool Register(int id, Hotkey hotkey, out int error)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        return NativeWindow.RegisterHotkey(_source.Handle, id, (int)hotkey.Modifiers, hotkey.VirtualKey, out error);
    }

    public void Unregister(int id) => NativeWindow.UnregisterHotkey(_source.Handle, id);

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeWindow.WmHotkey)
        {
            handled = true;
            Pressed?.Invoke(this, (int)wParam);
        }

        return 0;
    }
}
