using RigShift.Core.Fov;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class FovCalculatorTests
{
    /// <summary>A 27-inch 16:9 panel: 597.7 × 336.2 mm.</summary>
    private static readonly ScreenSize Panel27 = ScreenSize.FromDiagonal(27, 16, 9);

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
        FovResult result = FovCalculator.Calculate(new FovInput(Panel27, 600, Triple: false));

        result.HorizontalDegrees.ShouldBe(53.0, 0.1);
        result.VerticalDegrees.ShouldBe(31.3, 0.1);
        result.TripleAngleDegrees.ShouldBeNull();
        result.TripleHorizontalDegrees.ShouldBeNull();
    }

    [Fact]
    public void Calculate_Triples_AngleIncludesBothBezels_TotalIsThreeScreens()
    {
        FovResult result = FovCalculator.Calculate(new FovInput(Panel27, 600, Triple: true, BezelMm: 10));

        result.HorizontalDegrees.ShouldBe(53.0, 0.1);
        result.TripleAngleDegrees.ShouldNotBeNull().ShouldBe(54.5, 0.1);
        result.TripleHorizontalDegrees.ShouldNotBeNull().ShouldBe(158.9, 0.1);
    }

    [Fact]
    public void Calculate_UnknownSizeOrDistance_GivesZeroInsteadOfNaN()
    {
        FovCalculator.Calculate(new FovInput(new ScreenSize(0, 0), 600, Triple: false)).HorizontalDegrees.ShouldBe(0);
        FovCalculator.Calculate(new FovInput(Panel27, 0, Triple: true)).TripleAngleDegrees.ShouldBe(0);
        ScreenSize.FromDiagonal(-1, 16, 9).IsKnown.ShouldBeFalse();
    }

    [Fact]
    public void ForGames_EachSimGetsItsOwnKindOfAngle()
    {
        FovResult triple = FovCalculator.Calculate(new FovInput(Panel27, 600, Triple: true, BezelMm: 10));

        IReadOnlyList<GameFov> games = FovCalculator.ForGames(triple);

        games.Count.ShouldBe(FovCalculator.Games.Count);
        games.Single(g => g.Game == "Assetto Corsa Competizione").Degrees.ShouldBe(31.3, 0.1);
        games.Single(g => g.Game == "Automobilista 2").Degrees.ShouldBe(53.0, 0.1);
        games.Single(g => g.Game == "iRacing").Degrees.ShouldBe(158.9, 0.1);
        games.Single(g => g.Game.StartsWith("F1", StringComparison.Ordinal)).Degrees.ShouldBe(62.6, 0.1);

        // A single screen: iRacing takes the one screen's horizontal angle.
        FovCalculator.ForGames(FovCalculator.Calculate(new FovInput(Panel27, 600, Triple: false)))
            .Single(g => g.Game == "iRacing").Degrees.ShouldBe(53.0, 0.1);
    }
}
