using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace RigShift.Windows.Ui;

/// <summary>
/// An invisible window that hands WM_HOTKEY and WM_DISPLAYCHANGE to <see cref="MessageReceived"/>. Used where the UI
/// framework has no window procedure to hook. Create and use it on one thread.
/// </summary>
public sealed unsafe class MessageWindow : IDisposable
{
    private readonly string _className = "RigShift.MessageWindow." + Guid.NewGuid().ToString("N");

    // Kept in a field: the window class calls it for as long as the window lives, the GC must not collect it.
    private readonly WNDPROC _procedure;
    private HWND _handle;

    /// <param name="receivesBroadcasts">
    /// A hidden top-level window instead of a message-only one: broadcasts such as WM_DISPLAYCHANGE only reach top-level
    /// windows, and Windows names one when it shows why the app holds up logging off.
    /// </param>
    public MessageWindow(bool receivesBroadcasts = false)
    {
        _procedure = Procedure;
        fixed (char* name = _className)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                lpfnWndProc = _procedure,
                hInstance = PInvoke.GetModuleHandle((PCWSTR)null),
                lpszClassName = name,
            };
            if (PInvoke.RegisterClassEx(windowClass) == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Message window class not registered");
            }

            _handle = PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, receivesBroadcasts ? default : HWND.HWND_MESSAGE, default, windowClass.hInstance, null);
        }

        if (_handle.IsNull)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Message window not created");
        }
    }

    /// <summary>Message, wParam and lParam of each message; the window answers 0 to the ones it hands on.</summary>
    public event EventHandler<(uint Message, nint WParam, nint LParam)>? MessageReceived;

    public nint Handle => _handle;

    public void Dispose()
    {
        if (_handle.IsNull)
        {
            return;
        }

        PInvoke.DestroyWindow(_handle);
        _handle = default;
        fixed (char* name = _className)
        {
            PInvoke.UnregisterClass(name, PInvoke.GetModuleHandle((PCWSTR)null));
        }
    }

    private LRESULT Procedure(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message is NativeWindow.WmHotkey or NativeWindow.WmDisplayChange)
        {
            MessageReceived?.Invoke(this, (message, (nint)(nuint)wParam.Value, lParam.Value));
            return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
