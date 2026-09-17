namespace RigShift.Core.Fov;

/// <summary>Physical picture size of a display in millimetres, from the EDID or from a diagonal the user typed.</summary>
public sealed record ScreenSize(double WidthMm, double HeightMm)
{
    private const double MmPerInch = 25.4;

    public double DiagonalInches => Math.Sqrt((WidthMm * WidthMm) + (HeightMm * HeightMm)) / MmPerInch;

    public bool IsKnown => WidthMm > 0 && HeightMm > 0;

    /// <summary>A screen of the given diagonal with the aspect ratio of <paramref name="aspectWidth"/> : <paramref name="aspectHeight"/>.</summary>
    public static ScreenSize FromDiagonal(double diagonalInches, double aspectWidth, double aspectHeight)
    {
        if (diagonalInches <= 0 || aspectWidth <= 0 || aspectHeight <= 0)
        {
            return new ScreenSize(0, 0);
        }

        double diagonalMm = diagonalInches * MmPerInch;
        double factor = diagonalMm / Math.Sqrt((aspectWidth * aspectWidth) + (aspectHeight * aspectHeight));
        return new ScreenSize(aspectWidth * factor, aspectHeight * factor);
    }

    /// <summary>The same aspect ratio, scaled to another diagonal – when the user corrects the size the EDID reported.</summary>
    public ScreenSize WithDiagonal(double diagonalInches) => FromDiagonal(diagonalInches, WidthMm, HeightMm);
}

/// <summary>What the calculator needs: the picture, the eye distance and, for triples, the frame between two panels.</summary>
/// <param name="BezelMm">Width of one panel's frame; two of them sit between neighbouring pictures.</param>
public sealed record FovInput(ScreenSize Screen, double DistanceMm, bool Triple, double BezelMm = 0);

/// <summary>Angles in degrees. Triple values are <c>null</c> for a single screen.</summary>
/// <param name="TripleAngleDegrees">How far each side screen turns in, relative to the centre screen.</param>
/// <param name="TripleHorizontalDegrees">The horizontal angle all three pictures cover together.</param>
public sealed record FovResult(double VerticalDegrees, double HorizontalDegrees, double? TripleAngleDegrees, double? TripleHorizontalDegrees);

/// <summary>Which number a sim wants in its field-of-view setting.</summary>
public enum FovKind
{
    /// <summary>Vertical angle of one screen; stays the same on triples.</summary>
    Vertical,

    /// <summary>Horizontal angle of one screen; triples enter their side angle separately.</summary>
    HorizontalPerScreen,

    /// <summary>Horizontal angle of everything on screen, so three screens add up.</summary>
    HorizontalTotal,

    /// <summary>Twice the vertical angle – the Codemasters titles count that way.</summary>
    DoubleVertical,
}

/// <summary>One line of the "value per sim" table.</summary>
public sealed record GameFov(string Game, FovKind Kind, double Degrees);

/// <summary>
/// The usual 1:1 geometry: the picture is seen from the distance the eye really has, so the angle it covers on
/// screen equals the angle it would cover through a windscreen. Curved screens are treated as flat.
/// </summary>
public static class FovCalculator
{
    /// <summary>The sims with a known field-of-view convention, in the order the table shows them.</summary>
    public static IReadOnlyList<(string Game, FovKind Kind)> Games { get; } =
    [
        ("Assetto Corsa", FovKind.Vertical),
        ("Assetto Corsa Competizione", FovKind.Vertical),
        ("Assetto Corsa EVO", FovKind.Vertical),
        ("Automobilista 2", FovKind.HorizontalPerScreen),
        ("Le Mans Ultimate / rFactor 2", FovKind.Vertical),
        ("RaceRoom", FovKind.Vertical),
        ("iRacing", FovKind.HorizontalTotal),
        ("F1 2x / EA WRC", FovKind.DoubleVertical),
    ];

    public static FovResult Calculate(FovInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Screen.IsKnown || input.DistanceMm <= 0)
        {
            return new FovResult(0, 0, input.Triple ? 0 : null, input.Triple ? 0 : null);
        }

        double horizontal = Angle(input.Screen.WidthMm, input.DistanceMm);
        double vertical = Angle(input.Screen.HeightMm, input.DistanceMm);
        if (!input.Triple)
        {
            return new FovResult(vertical, horizontal, null, null);
        }

        // Side screens turned in until they sit on the same circle around the eye as the centre one; the frames
        // of two neighbours meet, so the turn covers the picture plus both frames.
        double angle = Angle(input.Screen.WidthMm + (2 * Math.Max(0, input.BezelMm)), input.DistanceMm);
        return new FovResult(vertical, horizontal, angle, 3 * horizontal);
    }

    public static IReadOnlyList<GameFov> ForGames(FovResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return [.. Games.Select(g => new GameFov(g.Game, g.Kind, ValueFor(result, g.Kind)))];
    }

    private static double ValueFor(FovResult result, FovKind kind) => kind switch
    {
        FovKind.Vertical => result.VerticalDegrees,
        FovKind.HorizontalPerScreen => result.HorizontalDegrees,
        FovKind.HorizontalTotal => result.TripleHorizontalDegrees ?? result.HorizontalDegrees,
        FovKind.DoubleVertical => 2 * result.VerticalDegrees,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    /// <summary>Angle a length covers when its middle is straight ahead at the given distance.</summary>
    private static double Angle(double lengthMm, double distanceMm) =>
        2 * Math.Atan(lengthMm / (2 * distanceMm)) * 180 / Math.PI;
}
