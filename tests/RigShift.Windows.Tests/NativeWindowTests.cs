using RigShift.Windows.Ui;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>The small Win32 helpers that read something and change nothing lasting.</summary>
public sealed class NativeWindowTests
{
    /// <summary>The hotkey field shows keys by the keyboard layout's names; Page Up must not read as the number pad's 9.</summary>
    [Fact]
    public void KeyName_LetterAndExtendedKey_HaveTheirOwnNames()
    {
        NativeWindow.KeyName(0x41).ShouldBe("A");
        string pageUp = NativeWindow.KeyName(0x21).ShouldNotBeNull();
        pageUp.ShouldNotBe(NativeWindow.KeyName(0x69));
        NativeWindow.KeyName(0xFF).ShouldBeNull("no key has this code");
    }

    [Fact]
    public void RegisterHotkey_TakenTwice_ReportsTheError_AndUnregisterFreesIt()
    {
        // Ctrl+Alt+Shift+Win+F24: no keyboard has it, so nothing else holds it.
        const int modifiers = 0x1 | 0x2 | 0x4 | 0x8;
        const int f24 = 0x87;
        using var first = new MessageWindow();
        using var second = new MessageWindow();

        NativeWindow.RegisterHotkey(first.Handle, 1, modifiers, f24, out int error).ShouldBeTrue();
        error.ShouldBe(0);
        try
        {
            NativeWindow.RegisterHotkey(second.Handle, 1, modifiers, f24, out error).ShouldBeFalse();
            error.ShouldBe(1409, "ERROR_HOTKEY_ALREADY_REGISTERED");
        }
        finally
        {
            NativeWindow.UnregisterHotkey(first.Handle, 1);
        }

        NativeWindow.RegisterHotkey(second.Handle, 1, modifiers, f24, out _).ShouldBeTrue();
        NativeWindow.UnregisterHotkey(second.Handle, 1);
    }

    [Fact]
    public void SmallIconSize_AndSystemColor_AnswerWithoutAWindow()
    {
        NativeWindow.SmallIconSize().ShouldBeGreaterThanOrEqualTo(16);
        NativeWindow.SystemColor(default).ShouldBeLessThanOrEqualTo(0xFFFFFFu);
    }
}
