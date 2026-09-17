using RigShift.Core.Fov;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>Every sim's own convention, against the values collected in <c>docs/analysis/fov/</c>.</summary>
public sealed class SimCatalogTests
{
    private static readonly ScreenSize Panel27 = ScreenSize.FromDiagonal(27, 16, 9);

    [Fact]
    public void Evaluate_SingleScreenAt60Cm_GivesEverySimItsOwnNumber()
    {
        IReadOnlyList<SimValue> values = Evaluate(Single());

        values.Count.ShouldBe(SimCatalog.Sims.Count);
        Value(values, "AssettoCorsa").ShouldBe("31");
        Value(values, "Acc").ShouldBe("31");
        Value(values, "LeMansUltimate").ShouldBe("31");
        Value(values, "Ams2").ShouldBe("53");
        Value(values, "IRacing").ShouldBe("53.0");
        Value(values, "Wrc").ShouldBe("27");
        Value(values, "DirtRally2").ShouldBe("63");
        Value(values, "Rbr").ShouldBe("0.715");
        Value(values, "F1").ShouldBe("-12");
    }

    [Fact]
    public void Evaluate_RaceRoom_ShowsBothBasesBecauseTheSourcesDisagree()
    {
        SimValue value = Evaluate(Single()).Single(v => v.Id == "RaceRoom");

        value.Value.ShouldBe("0.5");
        value.Alternative.ShouldBe("0.8");
        value.Confidence.ShouldBe(FovConfidence.Unclear);
    }

    [Fact]
    public void Evaluate_Triples_HorizontalSimsCountAllThreeScreens()
    {
        IReadOnlyList<SimValue> values = Evaluate(Triple());

        // A vertical value stays what one screen covers, however the rig turns.
        Value(values, "Acc").ShouldBe("31");
        Value(values, "IRacing").ShouldBe("158.9");
        Value(values, "Ams2").ShouldBe("53");
        Value(values, "Rennsport").ShouldBe("159");
    }

    [Fact]
    public void Evaluate_Triples_FillTheSimsOwnGeometryFields()
    {
        IReadOnlyList<SimValue> values = Evaluate(Triple());

        SimField width = Field(values, "Ams2", "physicalWidth");
        width.Value.ShouldBe("59.8");
        width.Unit.ShouldBe("cm");
        Field(values, "Ams2", "distanceToEye").Value.ShouldBe("60.0");
        Field(values, "Ams2", "angleFromForward").Value.ShouldBe("54.5");

        Field(values, "RFactor2", "ViewParams").Value.ShouldBe("(0.598, 0.336, 0.600, 0.000, 0.010)");
        Field(values, "RFactor2", "LeftView / RightView").Value.ShouldBe("(0.598, 0.336, 0.600, 54.476, 0.010)");

        Field(values, "IRacing", "MonitorWidth").Value.ShouldBe("618");
        Field(values, "IRacing", "ScreenWidth").Value.ShouldBe("598");
        Field(values, "IRacing", "ScreenAngles").Value.ShouldBe("54.5");

        // The frame is an angle in ETS2, not a length: 2·atan(10/600).
        Field(values, "Ets2", "r_multimon_border_fov_left / _right").Value.ShouldBe("2");
    }

    [Fact]
    public void Evaluate_CurvedScreen_GivesIRacingTheRadiusInsteadOfAnAngle()
    {
        IReadOnlyList<SimValue> values = Evaluate(Single() with { CurvatureRadiusMm = 1800 });

        Field(values, "IRacing", "MonitorType").Value.ShouldBe("curved");
        Field(values, "IRacing", "RadiusOfCurvature").Value.ShouldBe("1800");

        // The curve never reaches the vertical value, and the horizontal one stays the rendered angle.
        Value(values, "Acc").ShouldBe("31");
        Value(values, "Ams2").ShouldBe("53");
    }

    [Fact]
    public void Evaluate_SingleScreen_HasNoTripleFields()
    {
        Evaluate(Single()).ShouldAllBe(v => v.Id == "IRacing" || v.Fields.Count == 0);
    }

    [Fact]
    public void Evaluate_Comfort_OpensUpTheSimValuesOnly()
    {
        IReadOnlyList<SimValue> values = Evaluate(Single() with { Comfort = true });

        // 31.3° plus a tenth of itself, not plus five.
        Value(values, "Acc").ShouldBe("34");
        Value(values, "Ams2").ShouldBe("58");
    }

    [Fact]
    public void Evaluate_WithoutAUsableScreen_IsEmpty()
    {
        var input = new FovInput { Screen = new ScreenSize(0, 0), DistanceMm = 600 };

        SimCatalog.Evaluate(input, FovCalculator.Calculate(input)).ShouldBeEmpty();
    }

    [Fact]
    public void Sims_EveryIdIsUsedOnce()
    {
        SimCatalog.Sims.Select(s => s.Id).Distinct().Count().ShouldBe(SimCatalog.Sims.Count);
    }

    private static FovInput Single() => new() { Screen = Panel27, DistanceMm = 600 };

    private static FovInput Triple() => Single() with { Triple = true, BezelMm = 10, PixelWidth = 2560, PixelHeight = 1440 };

    private static IReadOnlyList<SimValue> Evaluate(FovInput input) => SimCatalog.Evaluate(input, FovCalculator.Calculate(input));

    private static string Value(IReadOnlyList<SimValue> values, string id) => values.Single(v => v.Id == id).Value;

    private static SimField Field(IReadOnlyList<SimValue> values, string id, string name) =>
        values.Single(v => v.Id == id).Fields.Single(f => f.Name == name);
}
