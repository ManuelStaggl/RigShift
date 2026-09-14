using System.Text.RegularExpressions;
using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed partial class GameTemplatesTests
{
    private static readonly string[] PlannedGames = ["lmu", "iracing", "acc", "acevo", "rfactor2", "ams2", "f1"];

    [Fact]
    public void BuiltIn_ContainsThePlannedGames()
    {
        PlannedGames.ShouldBeSubsetOf(GameTemplates.BuiltIn.Select(t => t.Id));
    }

    [Fact]
    public void BuiltIn_AreValid()
    {
        GameTemplates.BuiltIn.Select(t => t.Id).ShouldBeUnique();
        foreach (GameTemplate template in GameTemplates.BuiltIn)
        {
            IdPattern().IsMatch(template.Id).ShouldBeTrue(template.Id);
            template.Name.ShouldNotBeNullOrWhiteSpace(template.Id);
            template.Executables.ShouldNotBeEmpty(template.Id);
            template.Executables.ShouldAllBe(e => e.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !e.Contains('\\'), template.Id);
        }
    }

    [Theory]
    [InlineData(@"C:\Games\LMU\Le Mans Ultimate.exe", "Le Mans Ultimate")]
    [InlineData("\"AMS2AVX.EXE\"", "AMS2AVX")]
    [InlineData("rFactor2", "rFactor2")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Normalize_StripsFolderAndExtension(string? input, string? expected) =>
        ProcessNames.Normalize(input).ShouldBe(expected);

    [Fact]
    public void ProcessNamesOf_UnknownTemplate_IsEmpty() =>
        GameTemplates.ProcessNamesOf(new AutomationRule { TemplateId = "no-such-game" }).ShouldBeEmpty();

    [GeneratedRegex("^[a-z0-9-]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex IdPattern();
}
