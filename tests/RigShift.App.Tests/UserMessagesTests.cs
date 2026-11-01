using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using RigShift.App.Localization;
using RigShift.App.Services;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class UserMessagesTests
{
    [Fact]
    public void Describe_SharingViolation_NamesTheOtherProgram()
    {
        var locked = new IOException("The process cannot access the file 'C:\\Users\\x\\profiles\\Desk.json'", unchecked((int)0x80070020));

        UserMessages.Describe(locked).ShouldBe(Loc.Instance["Error_FileInUse"]);
    }

    [Fact]
    public void Describe_DiskFull_SaysSo()
    {
        UserMessages.Describe(new IOException("disk", unchecked((int)0x80070070))).ShouldBe(Loc.Instance["Error_DiskFull"]);
    }

    [Theory]
    [MemberData(nameof(KnownExceptions))]
    public void Describe_KnownException_GivesItsOwnText(Exception exception, string key)
    {
        UserMessages.Describe(exception).ShouldBe(Loc.Instance[key]);
    }

    public static TheoryData<Exception, string> KnownExceptions() => new()
    {
        { new UnauthorizedAccessException("Access to the path 'C:\\Users\\x' is denied."), "Error_NoAccess" },
        { new FileNotFoundException("gone"), "Error_NotFound" },
        { new DirectoryNotFoundException("gone"), "Error_NotFound" },
        { new InvalidDataException("zip"), "Error_Damaged" },
        { new JsonException("json"), "Error_Damaged" },
        { Marshal.GetExceptionForHR(unchecked((int)0x800401D0))!, "Error_ClipboardBusy" },
        { new Win32Exception(2), "Error_NotFound" },
        { new Win32Exception(1223), "Error_Cancelled" },
    };

    [Fact]
    public void Describe_Anything_Else_ShowsTheTypeAndWhereTheLogIs_NotTheRawText()
    {
        string text = UserMessages.Describe(new InvalidOperationException("C:\\Users\\secret\\thing"));

        text.ShouldBe(Loc.Format("Error_Unexpected", nameof(InvalidOperationException)));
        text.ShouldNotContain("secret");
    }

    [Theory]
    [InlineData(5, "Error_Switch5")]
    [InlineData(87, "Error_Switch87")]
    [InlineData(50, "Error_Switch50")]
    [InlineData(1610, "Error_Switch1610")]
    public void DescribeSwitchError_KnownCode_SaysWhatToDo(int code, string key)
    {
        UserMessages.DescribeSwitchError(code).ShouldBe(Loc.Instance[key]);
    }

    [Fact]
    public void DescribeSwitchError_OtherCode_NamesIt()
    {
        UserMessages.DescribeSwitchError(31).ShouldBe(Loc.Format("Error_WindowsCode", 31));
    }
}
