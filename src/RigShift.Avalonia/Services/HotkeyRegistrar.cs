using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;

namespace RigShift.Avalonia.Services;

/// <summary>Global hotkeys on an invisible message-only window: WM_HOTKEY goes to the window that registered the key.</summary>
internal sealed class HotkeyRegistrar : IHotkeyRegistrar
{
    private readonly MessageWindow _window = new();

    public HotkeyRegistrar()
    {
        _window.MessageReceived += (_, message) =>
        {
            if (message.Message == NativeWindow.WmHotkey)
            {
                Pressed?.Invoke(this, (int)message.WParam);
            }
        };
    }

    public event EventHandler<int>? Pressed;

    public bool Register(int id, Hotkey hotkey, out int win32Error)
    {
        ArgumentNullException.ThrowIfNull(hotkey);
        return NativeWindow.RegisterHotkey(_window.Handle, id, (int)hotkey.Modifiers, hotkey.VirtualKey, out win32Error);
    }

    public void Unregister(int id) => NativeWindow.UnregisterHotkey(_window.Handle, id);

    public void Dispose() => _window.Dispose();
}
