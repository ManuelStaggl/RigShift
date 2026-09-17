namespace RigShift.Core.Fov;

/// <summary>Physical picture size of a display in millimetres, from the EDID or from a diagonal the user typed.</summary>
/// <remarks>On a curved panel the EDID reports the arc length, which is exactly what the curvature formulas want.</remarks>
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

/// <summary>What the calculator needs: the picture, the eye distance, the curve and, for triples, frame and side angle.</summary>
public sealed record FovInput
{
    /// <summary>Picture size in millimetres; on a curved panel the width is the arc length.</summary>
    public required ScreenSize Screen { get; init; }

    /// <summary>Eye to the middle of the centre screen, perpendicular, in millimetres.</summary>
    public required double DistanceMm { get; init; }

    public bool Triple { get; init; }

    /// <summary>Width of one panel's frame; two of them sit between neighbouring pictures.</summary>
    public double BezelMm { get; init; }

    /// <summary>Curvature radius in millimetres (1000R = 1000); <c>0</c> is a flat panel.</summary>
    public double CurvatureRadiusMm { get; init; }

    /// <summary>How far the side screens really turn in; <c>null</c> uses the ideal angle.</summary>
    public double? SideAngleDegrees { get; init; }

    /// <summary>Horizontal pixels of one screen, for pixel density and the Surround numbers.</summary>
    public int PixelWidth { get; init; }

    /// <summary>Vertical pixels of one screen; some sims want them in their triple settings.</summary>
    public int PixelHeight { get; init; }

    /// <summary>
    /// Adds a little slack to the value each sim gets (never to the geometry): +5°, capped at +10 % – the two
    /// margins the guides name. <see cref="SimCatalog"/> applies it, <see cref="FovCalculator"/> ignores it.
    /// </summary>
    public bool Comfort { get; init; }
}

/// <summary>What the numbers cannot be trusted with; every one of them is shown next to the field that caused it.</summary>
[Flags]
public enum FovWarnings
{
    None = 0,

    /// <summary>Side screens beyond 65° – barely buildable, and past what iRacing accepts.</summary>
    SideAngleTooLarge = 1,

    /// <summary>The eye sits in or behind the plane of the panel's chord; the angle has no meaning there.</summary>
    EyeInsideCurve = 2,

    /// <summary>Under 40 pixels per degree the picture gets soft (60 = 20/20 vision).</summary>
    LowPixelDensity = 4,

    /// <summary>The turned-in side screen reaches past the eye – that arrangement cannot be built.</summary>
    TripleImpossible = 8,

    /// <summary>Strong curve on triples: the arcs only meet without a kink when the eye sits on the radius.</summary>
    CurvedTriple = 16,
}

/// <summary>
/// Every angle the page shows, in degrees. Triple values are <c>null</c> for a single screen.
/// </summary>
public sealed record FovResult
{
    /// <summary>Always from the picture height – never derived from a horizontal angle (that inflates curved panels).</summary>
    public double VerticalDegrees { get; init; }

    /// <summary>The horizontal angle the sims render: the picture width seen flat, centre of the screen 1:1.</summary>
    public double HorizontalDegrees { get; init; }

    /// <summary>What the eye really covers – on a curved panel more than <see cref="HorizontalDegrees"/>, otherwise the same.</summary>
    public double TrueHorizontalDegrees { get; init; }

    public bool IsCurved { get; init; }

    /// <summary>The angle the side screens would need to continue the centre picture seamlessly.</summary>
    public double? IdealAngleDegrees { get; init; }

    /// <summary>The angle the numbers were calculated with – the ideal one, or what the user measured.</summary>
    public double? SideAngleDegrees { get; init; }

    /// <summary>One side screen's own picture, from its inner to its outer edge.</summary>
    public double? SideScreenDegrees { get; init; }

    /// <summary>Outer edge to outer edge, frames included – how wide the rig stands in front of the eye.</summary>
    public double? TotalDegrees { get; init; }

    /// <summary>The picture the three screens really show, frames left out: centre plus both sides.</summary>
    public double? RenderedDegrees { get; init; }

    /// <summary>Distance between the two outer picture edges – the number to check with a tape measure.</summary>
    public double? OuterEdgeDistanceMm { get; init; }

    /// <summary>Horizontal pixels per degree; 60 matches 20/20 vision, under 40 the picture gets soft.</summary>
    public double PixelsPerDegree { get; init; }

    /// <summary>Pixels Surround has to hide behind each pair of frames.</summary>
    public int SurroundSeamPixels { get; init; }

    /// <summary>Width of the spanned desktop with both seams.</summary>
    public int SurroundWidthPixels { get; init; }

    public FovWarnings Warnings { get; init; }

    /// <summary>Size and distance were usable; otherwise every angle is zero.</summary>
    public bool IsValid { get; init; }
}

/// <summary>
/// The geometry behind the page. The picture is seen from the distance the eye really has, so an angle on screen
/// equals the angle it would cover through a windscreen. Curved panels use the arc: the EDID width is the arc
/// length, and the eye sees the chord's ends from closer than the middle of the picture.
/// </summary>
public static class FovCalculator
{
    private const double MinPixelsPerDegree = 40;
    private const double MaxBuildableAngle = 65;

    /// <summary>Comfort slack on the sim value: the +5° of the guides, never more than a tenth of the angle.</summary>
    public static double WithComfort(double degrees) => degrees <= 0 ? degrees : degrees + Math.Min(5, 0.1 * degrees);

    public static FovResult Calculate(FovInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Screen.IsKnown || input.DistanceMm <= 0)
        {
            return new FovResult();
        }

        double width = input.Screen.WidthMm;
        double distance = input.DistanceMm;
        double radius = input.CurvatureRadiusMm > 0 ? input.CurvatureRadiusMm : 0;
        double bezel = Math.Max(0, input.BezelMm);

        // Chord and sagitta of the arc; a flat panel is the chord itself with no bulge.
        double halfArc = radius > 0 ? width / (2 * radius) : 0;
        double halfChord = radius > 0 ? radius * Math.Sin(halfArc) : width / 2;
        double sagitta = radius > 0 ? radius * (1 - Math.Cos(halfArc)) : 0;
        double edgeDistance = distance - sagitta;

        FovWarnings warnings = FovWarnings.None;
        double horizontal = Degrees(2 * Math.Atan(width / (2 * distance)));
        double vertical = Degrees(2 * Math.Atan(input.Screen.HeightMm / (2 * distance)));
        double trueHorizontal;
        if (radius <= 0)
        {
            trueHorizontal = horizontal;
        }
        else if (edgeDistance <= 0)
        {
            warnings |= FovWarnings.EyeInsideCurve;
            trueHorizontal = 180;
        }
        else
        {
            trueHorizontal = Degrees(2 * Math.Atan2(halfChord, edgeDistance));
        }

        double pixelsPerDegree = input.PixelWidth > 0 && horizontal > 0 ? input.PixelWidth / horizontal : 0;
        if (pixelsPerDegree > 0 && pixelsPerDegree < MinPixelsPerDegree)
        {
            warnings |= FovWarnings.LowPixelDensity;
        }

        if (!input.Triple)
        {
            return new FovResult
            {
                VerticalDegrees = vertical,
                HorizontalDegrees = horizontal,
                TrueHorizontalDegrees = trueHorizontal,
                IsCurved = radius > 0,
                PixelsPerDegree = pixelsPerDegree,
                Warnings = warnings,
                IsValid = true,
            };
        }

        // The panel as it stands in the room: its chord is the length that turns around the hinge.
        double panel = 2 * halfChord;
        double ideal = radius > 0 ? trueHorizontal : Degrees(2 * Math.Atan(((panel / 2) + bezel) / distance));
        double angle = input.SideAngleDegrees ?? ideal;
        if (angle > MaxBuildableAngle)
        {
            warnings |= FovWarnings.SideAngleTooLarge;
        }

        if (radius is > 0 and <= 1000 && Math.Abs(distance - radius) > 1)
        {
            warnings |= FovWarnings.CurvedTriple;
        }

        // Hinge where the two frames meet, then the side panel: its own frame plus its picture.
        double radians = angle * Math.PI / 180;
        double hingeX = (panel / 2) + bezel;
        double outerX = hingeX + ((panel + bezel) * Math.Cos(radians));
        double outerY = edgeDistance - ((panel + bezel) * Math.Sin(radians));
        double innerX = hingeX + (bezel * Math.Cos(radians));
        double innerY = edgeDistance - (bezel * Math.Sin(radians));
        if (outerY <= 0 || innerY <= 0 || edgeDistance <= 0)
        {
            warnings |= FovWarnings.TripleImpossible;
            return new FovResult
            {
                VerticalDegrees = vertical,
                HorizontalDegrees = horizontal,
                TrueHorizontalDegrees = trueHorizontal,
                IsCurved = radius > 0,
                IdealAngleDegrees = ideal,
                SideAngleDegrees = angle,
                PixelsPerDegree = pixelsPerDegree,
                Warnings = warnings,
                IsValid = true,
            };
        }

        double half = Math.Atan2(outerX, outerY);
        double side = Degrees(half - Math.Atan2(innerX, innerY));
        double total = Degrees(2 * half);
        int seam = input.PixelWidth > 0 && width > 0 ? (int)Math.Round(2 * bezel * input.PixelWidth / width) : 0;

        return new FovResult
        {
            VerticalDegrees = vertical,
            HorizontalDegrees = horizontal,
            TrueHorizontalDegrees = trueHorizontal,
            IsCurved = radius > 0,
            IdealAngleDegrees = ideal,
            SideAngleDegrees = angle,
            SideScreenDegrees = side,
            TotalDegrees = total,
            RenderedDegrees = horizontal + (2 * side),
            OuterEdgeDistanceMm = 2 * outerX,
            PixelsPerDegree = pixelsPerDegree,
            SurroundSeamPixels = seam,
            SurroundWidthPixels = input.PixelWidth > 0 ? (3 * input.PixelWidth) + (2 * seam) : 0,
            Warnings = warnings,
            IsValid = true,
        };
    }

    /// <summary>Where the eye has to sit for a wanted angle – the calculation the other way round.</summary>
    public static double DistanceForDegrees(double widthMm, double degrees) =>
        widthMm > 0 && degrees is > 0 and < 180 ? widthMm / (2 * Math.Tan(degrees * Math.PI / 360)) : 0;

    private static double Degrees(double radians) => radians * 180 / Math.PI;
}
