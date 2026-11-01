using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using RigShift.App.Localization;
using RigShift.Core.Fov;

namespace RigShift.App.Controls;

/// <summary>
/// The rig seen from above: the head, the screens where they really stand as bars (joints on triples, a bow on curved
/// panels), the cone they cover as light from the eye, the angle as one arc, the distance and width as dimensions on a
/// 10 cm floor grid, and a small side view for the vertical angle. Every change glides into the next one – the
/// picture is the fastest way to see what a number does. The geometry comes from <see cref="RigGeometry"/>, so
/// picture and numbers can never drift apart.
/// </summary>
public sealed class RigDiagram : FrameworkElement
{
    public static readonly DependencyProperty InputProperty = DependencyProperty.Register(
        nameof(Input), typeof(FovInput), typeof(RigDiagram),
        new FrameworkPropertyMetadata(null, OnInputChanged));

    /// <summary>How far the eyes sit above the screen's middle, for the side view.</summary>
    public static readonly DependencyProperty VerticalOffsetMmProperty = DependencyProperty.Register(
        nameof(VerticalOffsetMm), typeof(double), typeof(RigDiagram),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ShowSideViewProperty = DependencyProperty.Register(
        nameof(ShowSideView), typeof(bool), typeof(RigDiagram),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

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

    public double VerticalOffsetMm
    {
        get => (double)GetValue(VerticalOffsetMmProperty);
        set => SetValue(VerticalOffsetMmProperty, value);
    }

    public bool ShowSideView
    {
        get => (bool)GetValue(ShowSideViewProperty);
        set => SetValue(ShowSideViewProperty, value);
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

        drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight)));

        // Floor grid in 10 cm steps from the eye: it gives the picture its scale without a single number.
        double step = frame.Scale * 100;
        if (step >= 8)
        {
            var grid = new Pen(Fill("RigShift.Color.TextPrimary", 0.045), 1);
            grid.Freeze();
            for (double x = frame.Eye.X % step; x < ActualWidth; x += step)
            {
                drawingContext.DrawLine(grid, new Point(x, 0), new Point(x, ActualHeight));
            }

            for (double y = frame.Eye.Y % step; y < ActualHeight; y += step)
            {
                drawingContext.DrawLine(grid, new Point(0, y), new Point(ActualWidth, y));
            }
        }

        // The cone: light from the eye fading towards the screens; side screens a step darker than the centre.
        double reach = frame.Outline.Max(p => (p - frame.Eye).Length);
        var light = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.Absolute,
            Center = frame.Eye,
            GradientOrigin = frame.Eye,
            RadiusX = reach * 1.15,
            RadiusY = reach * 1.15,
            GradientStops =
            {
                new GradientStop(Tint("RigShift.Color.Accent", 0.34), 0),
                new GradientStop(Tint("RigShift.Color.Accent", 0.0), 1),
            },
        };
        light.Freeze();
        int centre = frame.Screens.Count == 3 ? 1 : 0;
        for (int i = 0; i < frame.Screens.Count; i++)
        {
            drawingContext.PushOpacity(i == centre ? 1 : 0.55);
            drawingContext.DrawGeometry(light, null, Fan(frame.Eye, frame.Screens[i]));
            drawingContext.Pop();
        }

        var edge = new Pen(Fill("RigShift.Color.Accent", 0.6), 1.5);
        edge.Freeze();
        drawingContext.DrawLine(edge, frame.Eye, frame.Outline[0]);
        drawingContext.DrawLine(edge, frame.Eye, frame.Outline[^1]);

        // The screens: bars with round ends, joints where triples meet.
        for (int i = 0; i < frame.Screens.Count; i++)
        {
            bool hover = i == _hover;
            var pen = new Pen(Brush(hover ? "RigShift.Brush.Accent" : "RigShift.Brush.DisplayRail"), 5)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            drawingContext.DrawGeometry(null, pen, Polyline(frame.Screens[i]));
        }

        if (frame.Screens.Count == 3)
        {
            var joint = new Pen(Brush("RigShift.Brush.DisplayRail"), 1.5);
            joint.Freeze();
            foreach (Point point in new[] { frame.Screens[1][0], frame.Screens[1][^1] })
            {
                drawingContext.DrawEllipse(Brush("RigShift.Brush.Chrome"), joint, point, 4, 4);
            }
        }

        // Distance as a dimension line beside the eye's line of sight, the width over the centre screen.
        IReadOnlyList<Point> middle = frame.Screens[centre];
        double span = Math.Abs(middle[^1].X - middle[0].X);
        double dimensionX = frame.Eye.X + Math.Max(44, span * 0.28);
        var dimension = new Pen(Fill("RigShift.Color.TextSecondary", 0.6), 1);
        dimension.Freeze();
        double top = frame.Centre.Y + 8;
        drawingContext.DrawLine(dimension, new Point(dimensionX, top), new Point(dimensionX, frame.Eye.Y));
        drawingContext.DrawLine(dimension, new Point(dimensionX - 5, top), new Point(dimensionX + 5, top));
        drawingContext.DrawLine(dimension, new Point(dimensionX - 5, frame.Eye.Y), new Point(dimensionX + 5, frame.Eye.Y));
        Chip(drawingContext, frame.Distance, new Point(dimensionX, (top + frame.Eye.Y) / 2), accent: false);
        Chip(drawingContext, frame.Width, new Point((middle.Min(p => p.X) + middle.Max(p => p.X)) / 2, middle.Min(p => p.Y) - 20), accent: false);

        // The angle as one arc at the eye, with its value on a chip above the head.
        const double Radius = 34;
        Point from = frame.Eye + (Unit(frame.Outline[0] - frame.Eye) * Radius);
        Point to = frame.Eye + (Unit(frame.Outline[^1] - frame.Eye) * Radius);
        var arc = new StreamGeometry();
        using (StreamGeometryContext context = arc.Open())
        {
            context.BeginFigure(from, false, false);
            context.ArcTo(to, new Size(Radius, Radius), 0, frame.TotalDegrees > 180, SweepDirection.Clockwise, true, false);
        }

        arc.Freeze();
        var arcPen = new Pen(Brush("RigShift.Brush.Accent"), 2);
        arcPen.Freeze();
        drawingContext.DrawGeometry(null, arcPen, arc);

        // The head: a soft shadow, the head itself and a small nose for the direction of view.
        drawingContext.DrawEllipse(Fill("RigShift.Color.TextPrimary", 0.05), null, frame.Eye + new Vector(0, 18), 30, 9);
        var headPen = new Pen(Brush("RigShift.Brush.TextPrimary"), 1.5);
        headPen.Freeze();
        drawingContext.DrawEllipse(Brush("RigShift.Brush.Layer"), headPen, frame.Eye + new Vector(0, 4), 10, 10);
        var nose = new StreamGeometry();
        using (StreamGeometryContext context = nose.Open())
        {
            context.BeginFigure(frame.Eye + new Vector(-4.5, -4), true, true);
            context.LineTo(frame.Eye + new Vector(0, -10), true, false);
            context.LineTo(frame.Eye + new Vector(4.5, -4), true, false);
        }

        nose.Freeze();
        drawingContext.DrawGeometry(Brush("RigShift.Brush.TextPrimary"), null, nose);
        Chip(drawingContext, frame.Total, frame.Eye - new Vector(0, Radius + 20), accent: true);

        // Hover: the screen's own share of the angle next to it.
        if (_hover >= 0 && _hover < frame.Labels.Count && frame.Labels[_hover].Length > 0)
        {
            IReadOnlyList<Point> screen = frame.Screens[_hover];
            Point anchor = screen[screen.Count / 2];
            Vector inward = Unit(frame.Eye - anchor) * 26;
            Chip(drawingContext, frame.Labels[_hover], anchor + inward, accent: true);
        }

        if (ShowSideView && SideViewBox(frame) is { } box)
        {
            SideView(drawingContext, frame, box);
        }

        drawingContext.Pop();
    }

    /// <summary>
    /// A free corner for the side view: bottom right beside a single screen's cone, top right above a triple's. The
    /// top view never shrinks for it – without a free corner the side view is left out (the value is on the page).
    /// </summary>
    private Rect? SideViewBox(Frame frame)
    {
        const double W = 128;
        const double H = 136;
        // What the picture occupies: the cone with the screens, the head, the dimension line and the width chip.
        IReadOnlyList<Point> middle = frame.Screens[frame.Screens.Count == 3 ? 1 : 0];
        double dimensionX = frame.Eye.X + Math.Max(44, Math.Abs(middle[^1].X - middle[0].X) * 0.28);
        var used = new GeometryGroup();
        used.Children.Add(Fan(frame.Eye, frame.Outline));
        used.Children.Add(new RectangleGeometry(new Rect(frame.Eye.X - 34, frame.Eye.Y - 60, 68, 90)));
        used.Children.Add(new RectangleGeometry(new Rect(dimensionX - 34, frame.Centre.Y, 68, Math.Max(0, frame.Eye.Y - frame.Centre.Y))));
        used.Children.Add(new RectangleGeometry(new Rect(middle.Min(p => p.X) - 10, middle.Min(p => p.Y) - 34, Math.Abs(middle[^1].X - middle[0].X) + 20, 40)));
        foreach (var box in new[] { new Rect(ActualWidth - W - 12, ActualHeight - H - 12, W, H), new Rect(ActualWidth - W - 12, 12, W, H) })
        {
            var inflated = box;
            inflated.Inflate(10, 10);
            if (box.Left > 12 && used.FillContainsWithDetail(new RectangleGeometry(inflated)) == IntersectionDetail.Empty)
            {
                return box;
            }
        }

        return null;
    }

    /// <summary>The small side view in a free corner: screen height, eye height and the vertical angle.</summary>
    private void SideView(DrawingContext context, Frame frame, Rect box)
    {
        const double W = 128;
        const double H = 136;
        var border = new Pen(Brush("RigShift.Brush.Line"), 1);
        border.Freeze();
        context.DrawRoundedRectangle(Brush("RigShift.Brush.Page"), border, box, 8, 8);
        Text(context, Loc.Instance["Fov_SideView"], box.TopLeft + new Vector(10, 6), "RigShift.Brush.TextSecondary", 11, FontWeights.Normal);

        // Fit distance and screen height into the box; the eye sits on the left, the screen on the right.
        double usableW = W - 40;
        double usableH = H - 50;
        double scale = Math.Min(usableW / frame.DistanceMm, usableH / frame.ScreenHeightMm);
        double screenX = box.Right - 22;
        double midY = box.Top + 26 + (usableH / 2);
        double half = frame.ScreenHeightMm * scale / 2;
        var eye = new Point(screenX - (frame.DistanceMm * scale), midY - (VerticalOffsetMm * scale));
        var topEdge = new Point(screenX, midY - half);
        var bottomEdge = new Point(screenX, midY + half);

        var cone = new StreamGeometry();
        using (StreamGeometryContext c = cone.Open())
        {
            c.BeginFigure(eye, true, true);
            c.LineTo(topEdge, true, false);
            c.LineTo(bottomEdge, true, false);
        }

        cone.Freeze();
        context.DrawGeometry(Fill("RigShift.Color.Accent", 0.18), null, cone);
        var edge = new Pen(Fill("RigShift.Color.Accent", 0.6), 1);
        edge.Freeze();
        context.DrawLine(edge, eye, topEdge);
        context.DrawLine(edge, eye, bottomEdge);
        var rail = new Pen(Brush("RigShift.Brush.DisplayRail"), 4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        rail.Freeze();
        context.DrawLine(rail, topEdge, bottomEdge);
        context.DrawEllipse(Brush("RigShift.Brush.TextPrimary"), null, eye, 4, 4);
        Text(context, frame.Vertical, new Point(box.Left + 10, box.Bottom - 22), "RigShift.Brush.TextPrimary", 12, FontWeights.SemiBold);
    }

    private static StreamGeometry Fan(Point eye, IReadOnlyList<Point> screen)
    {
        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(eye, true, true);
            foreach (Point point in screen)
            {
                context.LineTo(point, true, false);
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Vector Unit(Vector v) => v.Length > 0 ? v / v.Length : new Vector(0, -1);

    /// <summary>A value on a small pill: accent-filled for the result, quiet for dimensions.</summary>
    private void Chip(DrawingContext context, string text, Point centre, bool accent)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        FormattedText formatted = Format(text, accent ? "RigShift.Brush.OnAccent" : "RigShift.Brush.TextPrimary", 12, FontWeights.SemiBold);
        double width = formatted.Width + 20;
        const double Height = 22;
        var rect = new Rect(centre.X - (width / 2), centre.Y - (Height / 2), width, Height);
        Pen? pen = null;
        if (!accent)
        {
            pen = new Pen(Brush("RigShift.Brush.Line"), 1);
            pen.Freeze();
        }

        context.DrawRoundedRectangle(Brush(accent ? "RigShift.Brush.Accent" : "RigShift.Brush.Layer"), pen, rect, Height / 2, Height / 2);
        context.DrawText(formatted, new Point(rect.Left + 10, rect.Top + ((Height - formatted.Height) / 2)));
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
        // Room above for the width chip and below for the head.
        const double Side = 34;
        const double Top = 46;
        const double Bottom = 40;
        double scale = Math.Min((ActualWidth - (2 * Side)) / (2 * maxX), (ActualHeight - Top - Bottom) / maxY);
        double centreX = ActualWidth / 2;
        double bottom = ActualHeight - Bottom;
        Point Map(FovPoint point) => new(centreX + (point.X * scale), bottom - (point.Y * scale));

        var screens = panels.Select(p => (IReadOnlyList<Point>)[.. p.Points.Select(Map)]).ToList();
        // Left, centre, right – the order the outline of the cone needs.
        var ordered = screens.Count == 3 ? new List<IReadOnlyList<Point>> { screens[1], screens[0], screens[2] } : screens;
        var outline = ordered.SelectMany(s => s).ToList();
        string centreLabel = Degrees(result.TrueHorizontalDegrees > 0 ? result.TrueHorizontalDegrees : result.HorizontalDegrees);
        string sideLabel = Degrees(result.SideScreenDegrees ?? 0);
        List<string> labels = screens.Count == 3 ? [sideLabel, centreLabel, sideLabel] : [centreLabel];

        return new Frame(
            [.. ordered],
            outline,
            new Point(centreX, bottom),
            Map(panels[0].Middle),
            string.Create(Loc.Instance.Culture, $"{input.DistanceMm / 10:0} cm"),
            Degrees(result.TotalDegrees ?? result.TrueHorizontalDegrees),
            labels)
        {
            Scale = scale,
            Width = string.Create(Loc.Instance.Culture, $"{input.Screen.WidthMm / 10:0.0} cm"),
            TotalDegrees = result.TotalDegrees ?? result.TrueHorizontalDegrees,
            Vertical = Degrees(result.VerticalDegrees),
            DistanceMm = input.DistanceMm,
            ScreenHeightMm = input.Screen.HeightMm,
        };
    }

    private Brush Brush(string key) => TryFindResource(key) as Brush ?? Brushes.Gray;

    private SolidColorBrush Fill(string colorKey, double opacity)
    {
        var brush = new SolidColorBrush(TryFindResource(colorKey) is Color color ? color : Colors.Gray) { Opacity = opacity };
        brush.Freeze();
        return brush;
    }

    private Color Tint(string colorKey, double alpha)
    {
        Color color = TryFindResource(colorKey) is Color c ? c : Colors.Gray;
        return Color.FromArgb((byte)Math.Round(alpha * 255), color.R, color.G, color.B);
    }

    private void Text(DrawingContext context, string text, Point at, string brushKey, double size, FontWeight weight)
    {
        if (!string.IsNullOrEmpty(text))
        {
            context.DrawText(Format(text, brushKey, size, weight), at);
        }
    }

    private FormattedText Format(string text, string brushKey, double size, FontWeight weight)
    {
        var typeface = new Typeface(TryFindResource("RigShift.Font.Text") as FontFamily ?? new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);
        return new FormattedText(text, Loc.Instance.Culture, FlowDirection.LeftToRight, typeface, size, Brush(brushKey), VisualTreeHelper.GetDpi(this).PixelsPerDip);
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
        /// <summary>Device pixels per millimetre.</summary>
        public double Scale { get; init; }

        public string Width { get; init; } = string.Empty;

        public double TotalDegrees { get; init; }

        public string Vertical { get; init; } = string.Empty;

        public double DistanceMm { get; init; }

        public double ScreenHeightMm { get; init; }

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
