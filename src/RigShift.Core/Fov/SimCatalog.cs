using System.Globalization;

namespace RigShift.Core.Fov;

/// <summary>Which number a sim wants in its field-of-view setting.</summary>
public enum FovKind
{
    /// <summary>Vertical angle of one screen; stays the same on triples.</summary>
    Vertical,

    /// <summary>Horizontal angle of one screen – on triples the centre one.</summary>
    HorizontalPerScreen,

    /// <summary>Horizontal angle of everything on screen, so three screens add up.</summary>
    HorizontalTotal,

    /// <summary>Twice the vertical angle – the Codemasters titles count that way.</summary>
    DoubleVertical,

    /// <summary>A factor on the sim's own base angle instead of degrees.</summary>
    Multiplier,

    /// <summary>A slider without a unit; the sim maps it to an angle itself.</summary>
    Slider,

    /// <summary>Degrees away from a fixed base angle, plus or minus.</summary>
    Offset,

    /// <summary>The horizontal angle of an imagined 4:3 picture, in radians.</summary>
    Radians,
}

/// <summary>How well the convention is backed up – shown in the row, never hidden.</summary>
public enum FovConfidence
{
    /// <summary>Two independent sources, developer statement or the game's own config.</summary>
    Proven,

    /// <summary>One primary source, or only secondary ones.</summary>
    Likely,

    /// <summary>Sources contradict each other; the value comes with a note.</summary>
    Unclear,
}

/// <summary>One field of a sim's own geometry settings, ready to type in.</summary>
/// <param name="Name">The name the game itself uses, so it can be found in the menu or the file.</param>
public sealed record SimField(string Name, string Value, string Unit);

/// <summary>One line of the "value per sim" table.</summary>
public sealed record SimValue
{
    /// <summary>Key for the localised "where to enter" line: <c>Fov_Where_&lt;Id&gt;</c>.</summary>
    public required string Id { get; init; }

    public required string Sim { get; init; }

    public required FovKind Kind { get; init; }

    /// <summary>The number to type in, formatted the way the sim wants it.</summary>
    public required string Value { get; init; }

    /// <summary>A second reading where the convention is not settled – RaceRoom's other base angle.</summary>
    public string? Alternative { get; init; }

    public FovConfidence Confidence { get; init; }

    /// <summary>The sim renders three views itself; otherwise the three screens carry one wide picture.</summary>
    public bool NativeTriple { get; init; }

    /// <summary>The sim's own geometry fields, filled in – empty when there is nothing else to enter.</summary>
    public IReadOnlyList<SimField> Fields { get; init; } = [];
}

/// <summary>
/// What every sim wants in its field-of-view setting, and which of its own geometry fields go with it. The numbers
/// are formatted invariantly on purpose: they are typed into game menus and config files, not read as prose.
/// </summary>
public static class SimCatalog
{
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    /// <summary>Every sim with a known convention, in the order the table shows them.</summary>
    public static IReadOnlyList<(string Id, string Sim)> Sims { get; } =
    [
        ("AssettoCorsa", "Assetto Corsa"),
        ("Acc", "Assetto Corsa Competizione"),
        ("AccEvo", "Assetto Corsa EVO"),
        ("Ams2", "Automobilista 2"),
        ("ProjectCars", "Project CARS 2 / 3"),
        ("RFactor2", "rFactor 2"),
        ("LeMansUltimate", "Le Mans Ultimate"),
        ("RaceRoom", "RaceRoom"),
        ("IRacing", "iRacing"),
        ("BeamNg", "BeamNG.drive"),
        ("Ets2", "Euro / American Truck Simulator 2"),
        ("F1", "F1 24 / F1 25"),
        ("Wrc", "EA Sports WRC"),
        ("DirtRally2", "DiRT Rally 2.0"),
        ("Forza", "Forza Motorsport"),
        ("Rennsport", "Rennsport"),
        ("KartKraft", "KartKraft"),
        ("Rbr", "Richard Burns Rally (RSF)"),
    ];

    public static IReadOnlyList<SimValue> Evaluate(FovInput input, FovResult result)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(result);
        if (!result.IsValid)
        {
            return [];
        }

        // The three angles a sim can ask for, with the comfort slack already in them.
        double vertical = Comfort(input, result.VerticalDegrees);
        double single = Comfort(input, result.HorizontalDegrees);
        double wide = Comfort(input, input.Triple ? result.RenderedDegrees ?? result.HorizontalDegrees : result.HorizontalDegrees);
        double radians = 2 * Math.Atan(4.0 / 3 * input.Screen.HeightMm / 2 / input.DistanceMm);
        if (input.Comfort)
        {
            radians = FovCalculator.WithComfort(radians * 180 / Math.PI) * Math.PI / 180;
        }

        var values = new List<SimValue>();
        foreach ((string id, string sim) in Sims)
        {
            values.Add(Build(id, sim, input, result, vertical, single, wide, radians));
        }

        return values;
    }

    private static SimValue Build(string id, string sim, FovInput input, FovResult result, double vertical, double single, double wide, double radians) => id switch
    {
        "AssettoCorsa" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result, [("Width", Mm(input.Screen.WidthMm), "mm"), ("Distance", Mm(input.DistanceMm), "mm"), ("Angle", Angle(result), "°"), ("Margins", Mm(input.BezelMm), "mm")]),
        },
        "Acc" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result, [("Distance", Mm(input.DistanceMm), "mm"), ("Width", Mm(input.Screen.WidthMm), "mm"), ("Bezel", Mm(input.BezelMm), "mm"), ("Angle", Angle(result), "°")]),
        },
        "AccEvo" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Likely,
            NativeTriple = true,
            Fields = Triple(input, result, [("Distance", Mm(input.DistanceMm), "mm"), ("Width", Mm(input.Screen.WidthMm), "mm"), ("Angle", Angle(result), "°"), ("Bezel", Mm(input.BezelMm), "mm")]),
        },
        "Ams2" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalPerScreen,
            Value = Whole(single),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("physicalWidth", Cm(input.Screen.WidthMm), "cm"),
                ("physicalHeight", Cm(input.Screen.HeightMm), "cm"),
                ("bezelLeft / bezelRight", Cm(input.BezelMm), "cm"),
                ("angleFromForward", Angle(result), "°"),
                ("distanceToEye", Cm(input.DistanceMm), "cm"),
                ("pixelWidth / pixelHeight", Pixels(input), string.Empty),
            ]),
        },
        "ProjectCars" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalPerScreen,
            Value = Whole(single),
            Confidence = FovConfidence.Likely,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("physicalWidth", Cm(input.Screen.WidthMm), "cm"),
                ("physicalHeight", Cm(input.Screen.HeightMm), "cm"),
                ("bezelLeft / bezelRight", Cm(input.BezelMm), "cm"),
                ("angleFromForward", Angle(result), "°"),
                ("distanceToEye", Cm(input.DistanceMm), "cm"),
            ]),
        },
        "RFactor2" or "LeMansUltimate" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("ViewParams", ViewParams(input, 0), string.Empty),
                ("LeftView / RightView", ViewParams(input, result.SideAngleDegrees ?? 0), string.Empty),
            ]),
        },
        "RaceRoom" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Multiplier,
            Value = Multiplier(vertical, 58),
            Alternative = Multiplier(vertical, 40),
            Confidence = FovConfidence.Unclear,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("screenSize (width × height)", Mm(input.Screen.WidthMm) + " × " + Mm(input.Screen.HeightMm), "mm"),
                ("screenBezel", Mm(input.BezelMm), "mm"),
                ("distanceToScreen", Mm(input.DistanceMm), "mm"),
                ("screenAngleLeft / screenAngleRight", Angle(result), "°"),
            ]),
        },
        "IRacing" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalTotal,
            Value = Decimal(wide),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = IRacingFields(input, result),
        },
        "BeamNg" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result, [("Left / Right Angle", Angle(result), "°"), ("Player Distance", Mm(input.DistanceMm), "mm")]),
        },
        "Ets2" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalPerScreen,
            Value = Whole(single),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("r_multimon_mode", "2", string.Empty),
                ("r_multimon_fov_horizontal", Whole(single), "°"),
                ("r_multimon_border_fov_left / _right", Whole(BezelAngle(input)), "°"),
            ]),
        },
        "F1" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Offset,
            Value = Offset(wide),
            Confidence = FovConfidence.Unclear,
            NativeTriple = false,
        },
        "Wrc" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Slider,
            Value = Slider(vertical),
            Confidence = FovConfidence.Proven,
            NativeTriple = false,
        },
        "DirtRally2" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.DoubleVertical,
            Value = Whole(2 * vertical),
            Confidence = FovConfidence.Likely,
            NativeTriple = false,
        },
        "Forza" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Vertical,
            Value = Whole(vertical),
            Confidence = FovConfidence.Likely,
            NativeTriple = false,
        },
        "Rennsport" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalTotal,
            Value = Whole(wide),
            Confidence = FovConfidence.Likely,
            NativeTriple = false,
        },
        "KartKraft" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.HorizontalPerScreen,
            Value = Whole(single),
            Confidence = FovConfidence.Likely,
            NativeTriple = true,
            Fields = Triple(input, result,
            [
                ("Width with bezel", Mm(input.Screen.WidthMm + (2 * input.BezelMm)), "mm"),
                ("Width", Mm(input.Screen.WidthMm), "mm"),
                ("Eye distance", Mm(input.DistanceMm), "mm"),
            ]),
        },
        "Rbr" => new SimValue
        {
            Id = id,
            Sim = sim,
            Kind = FovKind.Radians,
            Value = radians.ToString("0.000", Fixed),
            Confidence = FovConfidence.Proven,
            NativeTriple = true,
            Fields = Triple(input, result, [("angle", Angle(result), "°")]),
        },
        _ => throw new ArgumentOutOfRangeException(nameof(id), id, null),
    };

    /// <summary>iRacing also takes the curve itself, so those two fields show up on a single screen too.</summary>
    private static List<SimField> IRacingFields(FovInput input, FovResult result)
    {
        var fields = new List<SimField>();
        if (input.Triple)
        {
            fields.Add(new SimField("MonitorWidth", Mm(input.Screen.WidthMm + (2 * input.BezelMm)), "mm"));
            fields.Add(new SimField("ScreenWidth", Mm(input.Screen.WidthMm), "mm"));
            fields.Add(new SimField("BezelWidth", Mm(input.BezelMm), "mm"));
            fields.Add(new SimField("ScreenAngles", Angle(result), "°"));
            fields.Add(new SimField("ViewingDist", Mm(input.DistanceMm), "mm"));
        }

        if (input.CurvatureRadiusMm > 0)
        {
            fields.Add(new SimField("MonitorType", "curved", string.Empty));
            fields.Add(new SimField("RadiusOfCurvature", Mm(input.CurvatureRadiusMm), "mm"));
        }

        return fields;
    }

    private static IReadOnlyList<SimField> Triple(FovInput input, FovResult result, IReadOnlyList<(string Name, string Value, string Unit)> fields) =>
        input.Triple && result.SideAngleDegrees is not null
            ? [.. fields.Select(f => new SimField(f.Name, f.Value, f.Unit))]
            : [];

    /// <summary>rFactor 2 and Le Mans Ultimate take one tuple: width m, height m, eye distance m, side angle °, bezel m.</summary>
    private static string ViewParams(FovInput input, double angle) => string.Create(
        Fixed,
        $"({input.Screen.WidthMm / 1000:0.000}, {input.Screen.HeightMm / 1000:0.000}, {input.DistanceMm / 1000:0.000}, {angle:0.000}, {input.BezelMm / 1000:0.000})");

    /// <summary>ETS2 asks for the frame as an angle, not as a length.</summary>
    private static double BezelAngle(FovInput input) =>
        input.DistanceMm > 0 ? 2 * Math.Atan(input.BezelMm / input.DistanceMm) * 180 / Math.PI : 0;

    private static double Comfort(FovInput input, double degrees) => input.Comfort ? FovCalculator.WithComfort(degrees) : degrees;

    private static string Whole(double degrees) => Math.Round(degrees).ToString("0", Fixed);

    private static string Decimal(double degrees) => degrees.ToString("0.0", Fixed);

    private static string Mm(double value) => Math.Round(value).ToString("0", Fixed);

    private static string Cm(double value) => (value / 10).ToString("0.0", Fixed);

    private static string Angle(FovResult result) => (result.SideAngleDegrees ?? 0).ToString("0.0", Fixed);

    private static string Pixels(FovInput input) => string.Create(Fixed, $"{input.PixelWidth} × {input.PixelHeight}");

    /// <summary>RaceRoom counts in tenths of its own base angle and never goes under 0.5.</summary>
    private static string Multiplier(double vertical, double baseDegrees) =>
        Math.Max(0.5, Math.Round(vertical / baseDegrees, 1)).ToString("0.0", Fixed);

    /// <summary>The WRC slider runs 0–100 over 18°–68°, half a degree per step.</summary>
    private static string Slider(double vertical) =>
        Math.Clamp(Math.Round((vertical - 18) / 0.5), 0, 100).ToString("0", Fixed);

    /// <summary>F1 has no angle, only a step away from its base of about 77° at 16:9.</summary>
    private static string Offset(double horizontal)
    {
        double steps = Math.Clamp(Math.Round((horizontal - 77) / 2), -20, 20);
        return (steps > 0 ? "+" : string.Empty) + steps.ToString("0", Fixed);
    }
}
