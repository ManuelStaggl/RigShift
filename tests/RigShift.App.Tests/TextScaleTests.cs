using RigShift.App.Views;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>Windows' accessibility setting "Text size" (100–225 %) as the factor the windows grow by.</summary>
public sealed class TextScaleTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(100, 1.0)]
    [InlineData(150, 1.5)]
    [InlineData(225, 2.25)]
    public void Factor_FollowsTheSetting(int? percent, double expected)
    {
        TextScale.Factor(percent, roomWidth: 10000, roomHeight: 10000, minWidth: 1080, minHeight: 680).ShouldBe(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-50)]
    [InlineData(99)]
    public void Factor_NeverShrinks(int percent)
    {
        TextScale.Factor(percent, 10000, 10000, 1080, 680).ShouldBe(1.0);
    }

    [Fact]
    public void Factor_AboveTheSettingsRange_StopsAt225()
    {
        TextScale.Factor(900, 10000, 10000, 1080, 680).ShouldBe(2.25);
    }

    [Fact]
    public void Factor_StopsWhereTheSmallestWindowStillFitsTheScreen()
    {
        // 1920 x 1040 work area, smallest layout 1080 x 680: the height gives way first.
        TextScale.Factor(225, roomWidth: 1920, roomHeight: 1040, minWidth: 1080, minHeight: 680).ShouldBe(1040d / 680, 0.0001);
    }

    [Fact]
    public void Factor_ScreenSmallerThanTheWindow_StaysAtOne()
    {
        TextScale.Factor(150, roomWidth: 1024, roomHeight: 600, minWidth: 1080, minHeight: 680).ShouldBe(1.0);
    }

    [Fact]
    public void Factor_WindowWithoutMinimum_IsNotLimited()
    {
        TextScale.Factor(200, roomWidth: 1920, roomHeight: 1040, minWidth: 0, minHeight: 0).ShouldBe(2.0);
    }
}
