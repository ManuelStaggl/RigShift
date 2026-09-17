using RigShift.Core.Fov;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>
/// The reference values come from the research in <c>docs/analysis/fov/</c> – the same numbers the open calculators
/// and the curvature scripts produce.
/// </summary>
public sealed class FovCalculatorTests
{
    /// <summary>A 27-inch 16:9 panel: 597.7 × 336.2 mm.</summary>
    private static readonly ScreenSize Panel27 = ScreenSize.FromDiagonal(27, 16, 9);

    /// <summary>The Odyssey G9 as its EDID reports it: the arc is 1193 mm long.</summary>
    private static readonly ScreenSize PanelG9 = new(1193, 336);

    [Fact]
    public void FromDiagonal_27Inch16By9_IsTheUsualPanelSize()
    {
        Panel27.WidthMm.ShouldBe(597.7, 0.1);
        Panel27.HeightMm.ShouldBe(336.2, 0.1);
        Panel27.DiagonalInches.ShouldBe(27, 0.001);
        Panel27.WithDiagonal(32).DiagonalInches.ShouldBe(32, 0.001);
        (Panel27.WithDiagonal(32).WidthMm / Panel27.WithDiagonal(32).HeightMm).ShouldBe(16.0 / 9, 0.001);
    }

    [Fact]
    public void Calculate_SingleScreenAt60Cm_MatchesTheCommonCalculators()
    {
        FovResult result = FovCalculator.Calculate(Input(Panel27, 600));

        result.HorizontalDegrees.ShouldBe(53.0, 0.1);
        result.VerticalDegrees.ShouldBe(31.3, 0.1);
        result.TrueHorizontalDegrees.ShouldBe(53.0, 0.1);
        result.IsCurved.ShouldBeFalse();
        result.SideAngleDegrees.ShouldBeNull();
        result.TotalDegrees.ShouldBeNull();
    }

    [Fact]
    public void Calculate_TriplesWithoutBezels_AddUpToThreeScreens()
    {
        FovResult result = FovCalculator.Calculate(Input(Panel27, 600) with { Triple = true });

        result.IdealAngleDegrees.ShouldNotBeNull().ShouldBe(53.0, 0.1);
        result.TotalDegrees.ShouldNotBeNull().ShouldBe(158.9, 0.1);
        result.SideScreenDegrees.ShouldNotBeNull().ShouldBe(53.0, 0.1);
    }

    [Fact]
    public void Calculate_TriplesWithBezels_TurnFurtherIn_AndThePicturesStillAddUp()
    {
        FovResult result = FovCalculator.Calculate(Input(Panel27, 600) with { Triple = true, BezelMm = 10 });

        result.SideAngleDegrees.ShouldNotBeNull().ShouldBe(54.5, 0.1);

        // Edge to edge the frames count too; the picture itself is the three screens' own angles.
        result.TotalDegrees.ShouldNotBeNull().ShouldBe(161.9, 0.1);
        result.RenderedDegrees.ShouldNotBeNull().ShouldBe(158.9, 0.1);
        result.OuterEdgeDistanceMm.ShouldNotBeNull().ShouldBe(1324, 2);
    }

    [Theory]
    [InlineData(35, 144.9)]
    [InlineData(45, 153.6)]
    [InlineData(54, 161.0)]
    public void Calculate_MeasuredSideAngle_UsesTheHingeModel(double angle, double total)
    {
        FovResult result = FovCalculator.Calculate(Input(Panel27, 600) with { Triple = true, BezelMm = 7, SideAngleDegrees = angle });

        result.SideAngleDegrees.ShouldNotBeNull().ShouldBe(angle);
        result.TotalDegrees.ShouldNotBeNull().ShouldBe(total, 0.2);
    }

    [Theory]
    [InlineData(600, 105.5, 89.7, 31.3)]
    [InlineData(800, 83.7, 73.4, 23.7)]
    [InlineData(1000, 68.4, 61.6, 19.1)]
    public void Calculate_CurvedG9_SeesMoreThanItRenders(double distance, double real, double flat, double vertical)
    {
        FovResult result = FovCalculator.Calculate(Input(PanelG9, distance) with { CurvatureRadiusMm = 1000 });

        result.TrueHorizontalDegrees.ShouldBe(real, 0.1);
        result.HorizontalDegrees.ShouldBe(flat, 0.1);

        // Never derived from the horizontal angle – that is what inflates the value in other calculators.
        result.VerticalDegrees.ShouldBe(vertical, 0.1);
        result.IsCurved.ShouldBeTrue();
    }

    [Fact]
    public void Calculate_GentleCurve_BarelyChangesTheAngle()
    {
        FovResult result = FovCalculator.Calculate(Input(new ScreenSize(797, 335), 700) with { CurvatureRadiusMm = 1800 });

        result.TrueHorizontalDegrees.ShouldBe(62.2, 0.2);
        result.HorizontalDegrees.ShouldBe(59.3, 0.1);
    }

    [Theory]
    [InlineData(700, 57.1, 171.2)]
    [InlineData(1000, 40.6, 121.8)]
    public void Calculate_CurvedTriples_TurnInByWhatOneScreenReallyCovers(double distance, double angle, double total)
    {
        ScreenSize panel = ScreenSize.FromDiagonal(32, 16, 9);

        FovResult result = FovCalculator.Calculate(Input(panel, distance) with { Triple = true, CurvatureRadiusMm = 1000 });

        result.SideAngleDegrees.ShouldNotBeNull().ShouldBe(angle, 0.1);
        result.TotalDegrees.ShouldNotBeNull().ShouldBe(total, 0.2);
    }

    [Fact]
    public void Calculate_PixelsAndSurround_CountTheHiddenColumns()
    {
        FovResult result = FovCalculator.Calculate(Input(Panel27, 600) with { Triple = true, BezelMm = 9, PixelWidth = 2560 });

        result.PixelsPerDegree.ShouldBe(48.3, 0.1);
        result.SurroundSeamPixels.ShouldBe(77);
        result.SurroundWidthPixels.ShouldBe(7834);
    }

    [Fact]
    public void Calculate_ThingsThatCannotBeBuilt_AreFlagged()
    {
        // Eye inside the curve: the sagitta of a strong curve reaches past 30 cm.
        FovCalculator.Calculate(Input(PanelG9, 150) with { CurvatureRadiusMm = 1000 })
            .Warnings.HasFlag(FovWarnings.EyeInsideCurve).ShouldBeTrue();

        FovCalculator.Calculate(Input(Panel27, 400) with { Triple = true, BezelMm = 10 })
            .Warnings.HasFlag(FovWarnings.SideAngleTooLarge).ShouldBeTrue();

        FovCalculator.Calculate(Input(Panel27, 500) with { Triple = true, SideAngleDegrees = 80 })
            .Warnings.HasFlag(FovWarnings.TripleImpossible).ShouldBeTrue();

        FovCalculator.Calculate(Input(Panel27, 600) with { Triple = true, CurvatureRadiusMm = 1000 })
            .Warnings.HasFlag(FovWarnings.CurvedTriple).ShouldBeTrue();

        FovCalculator.Calculate(Input(Panel27, 600) with { PixelWidth = 1920 })
            .Warnings.HasFlag(FovWarnings.LowPixelDensity).ShouldBeTrue();
    }

    [Fact]
    public void Calculate_UnknownSizeOrDistance_GivesZeroInsteadOfNaN()
    {
        FovCalculator.Calculate(Input(new ScreenSize(0, 0), 600)).IsValid.ShouldBeFalse();
        FovCalculator.Calculate(Input(Panel27, 0) with { Triple = true }).HorizontalDegrees.ShouldBe(0);
        ScreenSize.FromDiagonal(-1, 16, 9).IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void DistanceForDegrees_IsTheCalculationTheOtherWayRound()
    {
        FovCalculator.DistanceForDegrees(Panel27.WidthMm, 53.0).ShouldBe(600, 2);
        FovCalculator.DistanceForDegrees(0, 53).ShouldBe(0);
    }

    [Fact]
    public void WithComfort_AddsFiveDegrees_ButNeverMoreThanATenth()
    {
        FovCalculator.WithComfort(100).ShouldBe(105);
        FovCalculator.WithComfort(31.3).ShouldBe(31.3 + 3.13, 0.001);
        FovCalculator.WithComfort(0).ShouldBe(0);
    }

    private static FovInput Input(ScreenSize screen, double distance) => new() { Screen = screen, DistanceMm = distance };
}
