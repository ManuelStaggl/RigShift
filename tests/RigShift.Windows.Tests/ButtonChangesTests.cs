using RigShift.Windows.Input;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class ButtonChangesTests
{
    private const nint Wheel = 0x1234;
    private const nint ButtonBox = 0x5678;
    private const uint Button1 = 0x0009_0001;
    private const uint Button2 = 0x0009_0002;
    private const uint Button5 = 0x0009_0005;

    [Fact]
    public void Update_FirstReportOfADevice_OnlySetsTheBaseline()
    {
        var changes = new ButtonChanges();

        changes.Update(Wheel, 0, []).ShouldBeFalse();
    }

    [Fact]
    public void Update_ButtonHeldBeforeListening_DoesNotCount()
    {
        // A latched toggle on a button box shows up in every report; it must not keep a switch on its own.
        var changes = new ButtonChanges();

        changes.Update(ButtonBox, 0, [Button5]).ShouldBeFalse();
        changes.Update(ButtonBox, 0, [Button5]).ShouldBeFalse();
    }

    [Fact]
    public void Update_PressAfterTheBaseline_Counts()
    {
        var changes = new ButtonChanges();
        changes.Update(Wheel, 0, []);

        changes.Update(Wheel, 0, [Button1]).ShouldBeTrue();
    }

    [Fact]
    public void Update_ReleaseOfTheFirstReportedPress_Counts()
    {
        // A device that reports only on change sends the press itself as its first report.
        var changes = new ButtonChanges();
        changes.Update(ButtonBox, 0, [Button1]);

        changes.Update(ButtonBox, 0, []).ShouldBeTrue();
    }

    [Fact]
    public void Update_SameButtonsInAnotherOrder_IsNoChange()
    {
        var changes = new ButtonChanges();
        changes.Update(Wheel, 0, [Button1, Button2]);

        changes.Update(Wheel, 0, [Button2, Button1]).ShouldBeFalse();
    }

    [Fact]
    public void Update_UnchangedReports_AreNoChange()
    {
        // Wheels report continuously while the axes move; only the buttons matter.
        var changes = new ButtonChanges();
        changes.Update(Wheel, 0, []);

        changes.Update(Wheel, 0, []).ShouldBeFalse();
        changes.Update(Wheel, 0, []).ShouldBeFalse();
    }

    [Fact]
    public void Update_FirstReportOfAnotherDevice_OnlySetsItsBaseline()
    {
        var changes = new ButtonChanges();
        changes.Update(Wheel, 0, []);

        changes.Update(ButtonBox, 0, [Button1]).ShouldBeFalse();
    }

    [Fact]
    public void Update_EachReportIdHasItsOwnBaseline()
    {
        var changes = new ButtonChanges();
        changes.Update(Wheel, 1, []);

        changes.Update(Wheel, 2, [Button1]).ShouldBeFalse();
        changes.Update(Wheel, 1, []).ShouldBeFalse();
        changes.Update(Wheel, 2, []).ShouldBeTrue();
    }
}
