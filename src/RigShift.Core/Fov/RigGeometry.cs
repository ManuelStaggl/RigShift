namespace RigShift.Core.Fov;

/// <summary>A point in the top view, in millimetres: the eye is the origin, x to the right, y straight ahead.</summary>
public readonly record struct FovPoint(double X, double Y);

/// <summary>One screen as it stands in the room: the picture from its inner to its outer edge.</summary>
/// <param name="Points">The picture line, curved panels as a polyline; the first and last point are the edges.</param>
public sealed record RigPanel(IReadOnlyList<FovPoint> Points)
{
    public FovPoint Left => Points[0];

    public FovPoint Right => Points[^1];

    /// <summary>Middle of the picture – where the label of this screen belongs.</summary>
    public FovPoint Middle => Points[Points.Count / 2];
}

/// <summary>
/// The rig seen from above, so the picture on the page is the same arrangement the numbers describe. Same model as
/// <see cref="FovCalculator"/>: the panel turns around the hinge where two frames meet.
/// </summary>
public static class RigGeometry
{
    private const int Segments = 24;

    /// <summary>Centre screen first, then left and right when the rig has three.</summary>
    public static IReadOnlyList<RigPanel> Panels(FovInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (!input.Screen.IsKnown || input.DistanceMm <= 0)
        {
            return [];
        }

        double width = input.Screen.WidthMm;
        double distance = input.DistanceMm;
        double radius = input.CurvatureRadiusMm > 0 ? input.CurvatureRadiusMm : 0;
        double bezel = Math.Max(0, input.BezelMm);
        double halfArc = radius > 0 ? width / (2 * radius) : 0;
        double halfChord = radius > 0 ? radius * Math.Sin(halfArc) : width / 2;
        double sagitta = radius > 0 ? radius * (1 - Math.Cos(halfArc)) : 0;
        double edge = distance - sagitta;

        var centre = Panel(new FovPoint(0, edge), 1, 0, halfChord, radius, halfArc);
        if (!input.Triple)
        {
            return [centre];
        }

        FovResult result = FovCalculator.Calculate(input);
        double angle = (result.SideAngleDegrees ?? 0) * Math.PI / 180;
        double panel = 2 * halfChord;
        double hingeX = halfChord + bezel;

        // Chord of the right screen: from its own inner edge outwards, the frame already behind it.
        double ux = Math.Cos(angle);
        double uy = -Math.Sin(angle);
        double innerX = hingeX + (bezel * ux);
        double innerY = edge + (bezel * uy);
        var middle = new FovPoint(innerX + (panel / 2 * ux), innerY + (panel / 2 * uy));
        var right = Panel(middle, ux, uy, halfChord, radius, halfArc);
        var left = new RigPanel([.. right.Points.Select(p => new FovPoint(-p.X, p.Y)).Reverse()]);
        return [centre, left, right];
    }

    /// <summary>One panel around the middle of its chord; the arc bulges away from the eye, as a concave screen does.</summary>
    private static RigPanel Panel(FovPoint middle, double ux, double uy, double halfChord, double radius, double halfArc)
    {
        if (radius <= 0)
        {
            return new RigPanel([
                new FovPoint(middle.X - (halfChord * ux), middle.Y - (halfChord * uy)),
                new FovPoint(middle.X + (halfChord * ux), middle.Y + (halfChord * uy))]);
        }

        // Normal of the chord, pointing away from the eye.
        double nx = -uy;
        double ny = ux;
        double cosHalf = Math.Cos(halfArc);
        var points = new List<FovPoint>(Segments + 1);
        for (int i = 0; i <= Segments; i++)
        {
            double phi = -halfArc + (2 * halfArc * i / Segments);
            double along = radius * Math.Sin(phi);
            double off = radius * (Math.Cos(phi) - cosHalf);
            points.Add(new FovPoint(middle.X + (along * ux) + (off * nx), middle.Y + (along * uy) + (off * ny)));
        }

        return new RigPanel(points);
    }
}
