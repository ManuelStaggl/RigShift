using RigShift.Core.Cli;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class CommandLineArgumentsTests
{
    [Theory]
    [InlineData("Rig", "\"Rig\"")]
    [InlineData("Sim Rig", "\"Sim Rig\"")]
    [InlineData("Rig\\", "\"Rig\\\\\"")]
    [InlineData("C:\\Rig", "\"C:\\Rig\"")]
    [InlineData("a\"b", "\"a\\\"b\"")]
    [InlineData("a\\\"b", "\"a\\\\\\\"b\"")]
    [InlineData("", "\"\"")]
    public void Quote_FollowsTheWindowsSplittingRules(string argument, string quoted) =>
        CommandLineArguments.Quote(argument).ShouldBe(quoted);
}
