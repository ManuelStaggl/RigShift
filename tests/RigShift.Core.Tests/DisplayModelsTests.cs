using RigShift.Core.Fov;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class DisplayModelsTests
{
    [Theory]
    [InlineData("Odyssey G93SC", 1800)]
    [InlineData("SAMSUNG LC49G95T", 1000)]
    [InlineData("Alienware AW3423DW", 1800)]
    [InlineData("LG 45GR95QE", 800)]
    public void RadiusFor_KnownMonitors_ComeFromTheTable(string name, int radius) =>
        DisplayModels.RadiusFor(name).ShouldBe(radius);

    [Fact]
    public void RadiusFor_TheOledG9_IsNotMixedUpWithTheOlderOne()
    {
        // G95SC is 1800R while G95C is 1000R – the longer name has to win.
        DisplayModels.RadiusFor("Odyssey OLED G95SC").ShouldBe(1800);
        DisplayModels.RadiusFor("Odyssey G95C").ShouldBe(1000);
    }

    [Fact]
    public void RadiusFor_AnythingElse_IsUnknownAndStaysFlat()
    {
        DisplayModels.RadiusFor("XG32UCWG").ShouldBeNull();
        DisplayModels.RadiusFor(null).ShouldBeNull();
        DisplayModels.RadiusFor(" ").ShouldBeNull();
    }
}
