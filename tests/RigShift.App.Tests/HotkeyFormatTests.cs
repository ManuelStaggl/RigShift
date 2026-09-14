using RigShift.App.Services;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class HotkeyFormatTests
{
    [Fact]
    public void Format_FunctionKey_KeepsItsName() =>
        HotkeyFormat.Format(new Hotkey { Modifiers = HotkeyModifiers.Alt, VirtualKey = 0x70 }).ShouldEndWith("+F1");

    [Theory]
    [InlineData(0xBC, "OemComma")]
    [InlineData(0x21, "Prior")]
    [InlineData(0x22, "Next")]
    public void Format_OemAndNavigationKeys_UseTheKeyboardLayoutName(int virtualKey, string internalName)
    {
        string text = HotkeyFormat.Format(new Hotkey { Modifiers = HotkeyModifiers.Alt, VirtualKey = virtualKey });

        text.ShouldStartWith("Alt+");
        text.ShouldNotContain(internalName);
        text.Length.ShouldBeGreaterThan("Alt+".Length);
    }
}
