using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RigShift.App.Localization;
using RigShift.Core.Fov;

namespace RigShift.App.Controls;

/// <summary>
/// The rig seen from above: the eye, the screens where they really stand, and the cone they cover. Every change
/// glides into the next one – the picture is the fastest way to see what a number does. The geometry itself comes
/// from <see cref="RigGeometry"/>, so picture and numbers can never drift apart.
/// </summary>
public sealed class RigDiagram : FrameworkElement
{
    public static readonly DependencyProperty InputProperty = DependencyProperty.Register(
        nameof(Input), typeof(FovInput), typeof(RigDiagram),
        new FrameworkPropertyMetadata(null, OnInputChanged));

    /// <summary>Runs 0 → 1 while the picture moves from the old arrangement to the new one.</summary>
    private static readonly DependencyProperty PhaseProperty = DependencyProperty.Register(
        nameof(Phase), typeof(double), typeof(RigDiagram),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration Glide = new(TimeSpan.FromMilliseconds(200));

    private Shape _from;
    private Shape _to;
    private int _hover = -1;

    public RigDiagram()
    {
        _from = default;
        _to = default;
    }

    /// <summary>The arrangement to draw; the control animates from whatever it currently shows.</summary>
    public FovInput? Input
    {
        get => (FovInput?)GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    private double Phase => (double)GetValue(PhaseProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        double width = double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width;
        double height = double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height;
        return new Size(width, height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point position = e.GetPosition(this);
        Frame frame = Build();
        int hover = -1;
        for (int i = 0; i < frame.Screens.Count; i++)
        {
            if (Near(frame.Screens[i], position))
            {
                hover = i;
                break;
            }
        }

        if (hover != _hover)
        {
            _hover = hover;
            InvalidateVisual();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        base.OnRender(drawingContext);
        Frame frame = Build();
        if (frame.Screens.Count == 0)
        {
            return;
        }

        // The cone first, so the screens sit on top of it.
        var cone = new StreamGeometry();
        using (StreamGeometryContext context = cone.Open())
        {
            context.BeginFigure(frame.Eye, true, true);
            foreach (Point point in frame.Outline)
            {
                context.LineTo(point, true, false);
            }
        }

        cone.Freeze();
        drawingContext.DrawGeometry(Fill("RigShift.Color.Accent", 0.13), null, cone);

        var line = new Pen(Brush("RigShift.Brush.Line"), 1) { DashStyle = new DashStyle([4, 3], 0) };
        line.Freeze();
        drawingContext.DrawLine(line, frame.Eye, frame.Centre);

        for (int i = 0; i < frame.Screens.Count; i++)
        {
            bool hover = i == _hover;
            var pen = new Pen(Brush(hover ? "RigShift.Brush.Accent" : "RigShift.Brush.LineStrong"), hover ? 5 : 3.5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, Polyline(frame.Screens[i]));
        }

        drawingContext.DrawEllipse(Brush("RigShift.Brush.TextPrimary"), null, frame.Eye, 4, 4);

        // Labels: the distance on its line, each screen's own angle at its middle, the whole rig under the eye.
        Text(drawingContext, frame.Distance, Middle(frame.Eye, frame.Centre) + new Vector(8, 0), "RigShift.Brush.TextSecondary", 12);
        for (int i = 0; i < frame.Screens.Count && i < frame.Labels.Count; i++)
        {
            Point anchor = frame.Screens[i][frame.Screens[i].Count / 2];
            Text(drawingContext, frame.Labels[i], anchor + new Vector(-14, -22), i == _hover ? "RigShift.Brush.Accent" : "RigShift.Brush.TextPrimary", 13);
        }

        Text(drawingContext, frame.Total, frame.Eye + new Vector(-24, 10), "RigShift.Brush.TextSecondary", 12);
    }

    private static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var diagram = (RigDiagram)d;
        Shape target = Shape.Of(e.NewValue as FovInput);
        if (!diagram._to.IsKnown || !target.IsKnown || diagram._to.Triple != target.Triple)
        {
            // Nothing to glide from: the first picture, an unusable one, or a different number of screens.
            diagram._from = target;
            diagram._to = target;
            diagram.BeginAnimation(PhaseProperty, null);
            diagram.SetValue(PhaseProperty, 1.0);
            diagram.InvalidateVisual();
            return;
        }

        diagram._from = Shape.Lerp(diagram._from, diagram._to, diagram.Phase);
        diagram._to = target;
        diagram.BeginAnimation(PhaseProperty, new DoubleAnimation(0, 1, Glide)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static Point Middle(Point a, Point b) => new((a.X + b.X) / 2, (a.Y + b.Y) / 2);

    private static StreamGeometry Polyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(points[0], false, false);
            for (int i = 1; i < points.Count; i++)
            {
                context.LineTo(points[i], true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static bool Near(IReadOnlyList<Point> points, Point position)
    {
        for (int i = 1; i < points.Count; i++)
        {
            if (DistanceTo(points[i - 1], points[i], position) < 10)
            {
                return true;
            }
        }

        return false;
    }

    private static double DistanceTo(Point a, Point b, Point p)
    {
        Vector segment = b - a;
        double length = segment.LengthSquared;
        double t = length <= 0 ? 0 : Math.Clamp(((p - a) * segment) / length, 0, 1);
        return (p - (a + (t * segment))).Length;
    }

    /// <summary>The picture for the current moment of the animation, in device pixels.</summary>
    private Frame Build()
    {
        Shape shape = Shape.Lerp(_from, _to, Phase);
        FovInput? input = shape.ToInput();
        if (input is null || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return Frame.Empty;
        }

        IReadOnlyList<RigPanel> panels = RigGeometry.Panels(input);
        FovResult result = FovCalculator.Calculate(input);
        if (panels.Count == 0)
        {
            return Frame.Empty;
        }

        // Everything the eye sees, plus the eye itself, has to fit; the picture keeps its proportions.
        double maxX = panels.SelectMany(p => p.Points).Max(p => Math.Abs(p.X));
        double maxY = panels.SelectMany(p => p.Points).Max(p => p.Y);
        const double Padding = 34;
        double scale = Math.Min((ActualWidth - (2 * Padding)) / (2 * maxX), (ActualHeight - (2 * Padding)) / maxY);
        double centreX = ActualWidth / 2;
        double bottom = ActualHeight - Padding;
        Point Map(FovPoint point) => new(centreX + (point.X * scale), bottom - (point.Y * scale));

        var screens = panels.Select(p => (IReadOnlyList<Point>)[.. p.Points.Select(Map)]).ToList();
        // Left, centre, right – the order the outline of the cone needs.
        var ordered = screens.Count == 3 ? new List<IReadOnlyList<Point>> { screens[1], screens[0], screens[2] } : screens;
        var outline = ordered.SelectMany(s => s).ToList();
        string centreLabel = Degrees(result.HorizontalDegrees);
        string sideLabel = Degrees(result.SideScreenDegrees ?? 0);
        List<string> labels = screens.Count == 3 ? [sideLabel, centreLabel, sideLabel] : [centreLabel];

        return new Frame(
            [.. ordered],
            outline,
            new Point(centreX, bottom),
            Map(panels[0].Middle),
            string.Create(Loc.Instance.Culture, $"{input.DistanceMm / 10:0} cm"),
            Degrees(result.TotalDegrees ?? result.HorizontalDegrees),
            labels);
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private SolidColorBrush Fill(string colorKey, double opacity)
    {
        var brush = new SolidColorBrush(TryFindResource(colorKey) is Color color ? color : Colors.Gray) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private void Text(DrawingContext context, string text, Point at, string brushKey, double size)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var typeface = new Typeface(TryFindResource("RigShift.Font.Text") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var formatted = new FormattedText(text, Loc.Instance.Culture, FlowDirection.LeftToRight, typeface, size, Brush(brushKey), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        context.DrawText(formatted, at);
    }

    private static string Degrees(double value) => value > 0 ? value.ToString("0.0", Loc.Instance.Culture) + "°" : string.Empty;

    /// <summary>One drawn picture: the screens, the cone's outline, the eye and the labels that belong to them.</summary>
    private sealed record Frame(
        IReadOnlyList<IReadOnlyList<Point>> Screens,
        IReadOnlyList<Point> Outline,
        Point Eye,
        Point Centre,
        string Distance,
        string Total,
        IReadOnlyList<string> Labels)
    {
        public static Frame Empty { get; } = new([], [], default, default, string.Empty, string.Empty, []);
    }

    /// <summary>
    /// The few numbers the picture is made of. The curve is carried as 1/R so a flat panel (no radius at all) and a
    /// curved one are the two ends of one straight line – otherwise switching the curve on would jump.
    /// </summary>
    private readonly record struct Shape(double Width, double Height, double Distance, double Curvature, double Bezel, double Angle, bool Triple, int PixelWidth, int PixelHeight)
    {
        public bool IsKnown => Width > 0 && Height > 0 && Distance > 0;

        public static Shape Of(FovInput? input)
        {
            if (input is null || !input.Screen.IsKnown || input.DistanceMm <= 0)
            {
                return default;
            }

            FovResult result = FovCalculator.Calculate(input);
            return new Shape(
                input.Screen.WidthMm,
                input.Screen.HeightMm,
                input.DistanceMm,
                input.CurvatureRadiusMm > 0 ? 1 / input.CurvatureRadiusMm : 0,
                input.BezelMm,
                result.SideAngleDegrees ?? 0,
                input.Triple,
                input.PixelWidth,
                input.PixelHeight);
        }

        public static Shape Lerp(Shape from, Shape to, double phase)
        {
            if (!from.IsKnown)
            {
                return to;
            }

            double t = Math.Clamp(phase, 0, 1);
            double Mix(double a, double b) => a + ((b - a) * t);
            return new Shape(
                Mix(from.Width, to.Width),
                Mix(from.Height, to.Height),
                Mix(from.Distance, to.Distance),
                Mix(from.Curvature, to.Curvature),
                Mix(from.Bezel, to.Bezel),
                Mix(from.Angle, to.Angle),
                to.Triple,
                to.PixelWidth,
                to.PixelHeight);
        }

        public FovInput? ToInput() => IsKnown
            ? new FovInput
            {
                Screen = new ScreenSize(Width, Height),
                DistanceMm = Distance,
                Triple = Triple,
                BezelMm = Bezel,
                CurvatureRadiusMm = Curvature > 0 ? 1 / Curvature : 0,
                SideAngleDegrees = Angle > 0 ? Angle : null,
                PixelWidth = PixelWidth,
                PixelHeight = PixelHeight,
            }
            : null;
    }
}
