using RigShift.Core.Fov;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>The picture is drawn from these points, so they have to agree with the angles the calculator reports.</summary>
public sealed class RigGeometryTests
{
    private static readonly ScreenSize Panel27 = ScreenSize.FromDiagonal(27, 16, 9);

    [Fact]
    public void Panels_SingleFlatScreen_IsTheChordAtTheEyeDistance()
    {
        IReadOnlyList<RigPanel> panels = RigGeometry.Panels(new FovInput { Screen = Panel27, DistanceMm = 600 });

        panels.Count.ShouldBe(1);
        panels[0].Points.Count.ShouldBe(2);
        panels[0].Left.X.ShouldBe(-298.9, 0.1);
        panels[0].Right.X.ShouldBe(298.9, 0.1);
        panels[0].Left.Y.ShouldBe(600, 0.1);
    }

    [Fact]
    public void Panels_CurvedScreen_BulgesTowardsTheEyeAtItsEdges()
    {
        IReadOnlyList<RigPanel> panels = RigGeometry.Panels(new FovInput { Screen = new ScreenSize(1193, 336), DistanceMm = 600, CurvatureRadiusMm = 1000 });

        FovPoint middle = panels[0].Middle;
        middle.X.ShouldBe(0, 0.1);
        middle.Y.ShouldBe(600, 0.5);

        // Chord ends: half chord 561.7 mm, sagitta 172.7 mm closer to the eye.
        panels[0].Right.X.ShouldBe(561.7, 1);
        panels[0].Right.Y.ShouldBe(427.3, 1);
    }

    [Fact]
    public void Panels_Triples_AreMirroredAndEndWhereTheAnglesSay()
    {
        var input = new FovInput { Screen = Panel27, DistanceMm = 600, Triple = true, BezelMm = 10 };
        FovResult result = FovCalculator.Calculate(input);

        IReadOnlyList<RigPanel> panels = RigGeometry.Panels(input);

        panels.Count.ShouldBe(3);
        panels[1].Left.X.ShouldBe(-panels[2].Right.X, 0.001);

        // The outer edges are exactly as far apart as the calculator's tape-measure number.
        (panels[2].Right.X - panels[1].Left.X).ShouldBe(result.OuterEdgeDistanceMm!.Value, 0.5);

        // And they are seen under the reported total angle.
        double half = Math.Atan2(panels[2].Right.X, panels[2].Right.Y) * 180 / Math.PI;
        (2 * half).ShouldBe(result.TotalDegrees!.Value, 0.1);
    }

    [Fact]
    public void Panels_WithoutAUsableScreen_AreEmpty()
    {
        RigGeometry.Panels(new FovInput { Screen = new ScreenSize(0, 0), DistanceMm = 600 }).ShouldBeEmpty();
        RigGeometry.Panels(new FovInput { Screen = Panel27, DistanceMm = 0 }).ShouldBeEmpty();
    }
}
