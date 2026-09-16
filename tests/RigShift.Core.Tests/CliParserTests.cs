using RigShift.Core.Cli;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class CliParserTests
{
    [Fact]
    public void Parse_NoArguments_StartsTheApp()
    {
        CliParseResult result = CliParser.Parse([]);

        result.Request.ShouldNotBeNull().Command.ShouldBe(CliCommand.None);
    }

    [Fact]
    public void Parse_Minimized_StartsTheAppInTheTray()
    {
        CliParser.Parse(["--minimized"]).Request.ShouldNotBeNull().Minimized.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ApplyWithOptions()
    {
        CliRequest request = CliParser.Parse(["apply", "Sim Rig", "--no-confirm", "--dry-run"]).Request.ShouldNotBeNull();

        request.Command.ShouldBe(CliCommand.Apply);
        request.ProfileName.ShouldBe("Sim Rig");
        request.NoConfirm.ShouldBeTrue();
        request.DryRun.ShouldBeTrue();
    }

    [Theory]
    [InlineData("list", CliCommand.List)]
    [InlineData("status", CliCommand.Status)]
    [InlineData("surround", CliCommand.Surround)]
    [InlineData("games", CliCommand.Games)]
    public void Parse_CommandsWithoutArguments(string command, CliCommand expected)
    {
        CliParser.Parse([command]).Request.ShouldNotBeNull().Command.ShouldBe(expected);
    }

    [Fact]
    public void Parse_ToggleWithOptions()
    {
        CliRequest request = CliParser.Parse(["toggle", "--no-confirm", "--dry-run", "--from-link"]).Request.ShouldNotBeNull();

        request.Command.ShouldBe(CliCommand.Toggle);
        request.ProfileName.ShouldBeNull();
        request.NoConfirm.ShouldBeTrue();
        request.DryRun.ShouldBeTrue();
        request.FromLink.ShouldBeTrue();
    }

    [Fact]
    public void Parse_ToggleWithAName_IsAnError()
    {
        CliParser.Parse(["toggle", "Rig"]).ExitCode.ShouldBe(CliExitCodes.InvalidArguments);
    }

    [Fact]
    public void Parse_Save_TakesTheName()
    {
        CliRequest request = CliParser.Parse(["save", "Desk"]).Request.ShouldNotBeNull();

        request.Command.ShouldBe(CliCommand.Save);
        request.ProfileName.ShouldBe("Desk");
    }

    [Fact]
    public void Parse_Play_TakesTheGameName()
    {
        CliRequest request = CliParser.Parse(["play", "iRacing", "--from-link"]).Request.ShouldNotBeNull();

        request.Command.ShouldBe(CliCommand.Play);
        request.GameName.ShouldBe("iRacing");
        request.ProfileName.ShouldBeNull();
        request.FromLink.ShouldBeTrue();
    }

    [Theory]
    [InlineData("apply")]
    [InlineData("play")]
    [InlineData("frobnicate")]
    public void Parse_InvalidArguments_ReturnsUsageText(string command)
    {
        CliParseResult result = CliParser.Parse([command]);

        result.Request.ShouldBeNull();
        result.ExitCode.ShouldBe(CliExitCodes.InvalidArguments);
        result.Output.ShouldNotBeEmpty();
    }

    [Fact]
    public void Parse_Help_PrintsCommands()
    {
        CliParseResult result = CliParser.Parse(["--help"]);

        result.Request.ShouldBeNull();
        result.ExitCode.ShouldBe(0);
        result.Output.ShouldContain("apply");
        result.Output.ShouldContain("status");
    }
}
