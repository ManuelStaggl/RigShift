using RigShift.Core.Cli;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class RigShiftUriTests
{
    [Theory]
    [InlineData("rigshift://apply/Rig", "Rig")]
    [InlineData("rigshift://apply/Rig/", "Rig")]
    [InlineData("RIGSHIFT://Apply/Sim%20Rig", "Sim Rig")]
    [InlineData("rigshift://apply/Schreibtisch%20%C3%BC", "Schreibtisch ü")]
    public void ToArguments_ApplyLink_BecomesApplyCommand(string uri, string name)
    {
        RigShiftUri.ToArguments(uri).ShouldBe(["apply", name]);
    }

    [Theory]
    [InlineData("rigshift://apply/")]
    [InlineData("rigshift://apply")]
    [InlineData("rigshift://save/Rig")]
    [InlineData("rigshift://apply/Rig/extra")]
    [InlineData("rigshift://apply/Rig?no-confirm=1")]
    [InlineData("rigshift://apply/Rig#x")]
    [InlineData("https://apply/Rig")]
    [InlineData("rigshift:")]
    public void ToArguments_AnythingElse_IsRejected(string uri)
    {
        RigShiftUri.ToArguments(uri).ShouldBeNull();
    }

    [Fact]
    public void ToArguments_ResultIsAValidCommandLine()
    {
        CliRequest request = CliParser.Parse(RigShiftUri.ToArguments("rigshift://apply/Sim%20Rig")!).Request.ShouldNotBeNull();

        request.Command.ShouldBe(CliCommand.Apply);
        request.ProfileName.ShouldBe("Sim Rig");
        request.NoConfirm.ShouldBeFalse();
    }

    [Theory]
    [InlineData("rigshift://apply/Rig", true)]
    [InlineData("RigShift:x", true)]
    [InlineData("apply", false)]
    [InlineData("--minimized", false)]
    public void IsUri_RecognizesTheScheme(string argument, bool expected)
    {
        RigShiftUri.IsUri(argument).ShouldBe(expected);
    }
}
