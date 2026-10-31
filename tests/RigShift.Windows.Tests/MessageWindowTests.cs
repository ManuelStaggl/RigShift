using RigShift.Windows.Ui;
using Shouldly;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>The hidden window behind the hotkeys (message-only) and the display change watcher (top-level).</summary>
public sealed class MessageWindowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HandsOnHotkeysAndDisplayChanges_AndNothingElse(bool receivesBroadcasts)
    {
        using var window = new MessageWindow(receivesBroadcasts);
        var received = new List<(uint Message, nint WParam)>();
        window.MessageReceived += (_, message) => received.Add((message.Message, message.WParam));

        // Sent from the window's own thread, the message goes straight to its window procedure.
        Send(window.Handle, NativeWindow.WmHotkey, 7);
        Send(window.Handle, NativeWindow.WmDisplayChange, 32);
        Send(window.Handle, PInvoke.WM_NULL, 0);

        received.ShouldBe([((uint)NativeWindow.WmHotkey, 7), ((uint)NativeWindow.WmDisplayChange, 32)]);
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        var window = new MessageWindow();
        window.Handle.ShouldNotBe(0);

        window.Dispose();
        window.Dispose();

        window.Handle.ShouldBe(0);
    }

    private static void Send(nint handle, uint message, nuint wParam) =>
        PInvoke.SendMessageTimeout(new HWND(handle), message, wParam, default, SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK, 1000);
}
