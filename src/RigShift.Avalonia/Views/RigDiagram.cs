using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using RigShift.App.Localization;
using RigShift.Core.Fov;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The rig seen from above: the head, the screens where they really stand as bars (joints on
/// triples, a bow on curved panels), the cone they cover as light from the eye, the angle as one arc, the distance and
/// width as dimensions on a 10 cm floor grid, and a small side view for the vertical angle. A change glides into the
/// next one. The geometry comes from <see cref="RigGeometry"/>, so picture and numbers can never drift apart.
/// </summary>
public sealed class RigDiagram : Control
{
    public static readonly StyledProperty<FovInput?> InputProperty =
        AvaloniaProperty.Register<RigDiagram, FovInput?>(nameof(Input));

    /// <summary>How far the eyes sit above the screen's middle, for the side view.</summary>
    public static readonly StyledProperty<double> VerticalOffsetMmProperty =
        AvaloniaProperty.Register<RigDiagram, double>(nameof(VerticalOffsetMm));

    private static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(200);

    private Shape _from;
    private Shape _to;
    private double _phase = 1;
    private DateTime _glideStart;
    private int _hover = -1;

    static RigDiagram() => AffectsRender<RigDiagram>(VerticalOffsetMmProperty);

    public RigDiagram()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The arrangement to draw; the control glides from whatever it currently shows.</summary>
    public FovInput? Input
    {
        get => GetValue(InputProperty);
        set => SetValue(InputProperty, value);
    }

    public double VerticalOffsetMm
    {
        get => GetValue(VerticalOffsetMmProperty);
        set => SetValue(VerticalOffsetMmProperty, value);
    }

    private double ActualWidth => Bounds.Width;

    private double ActualHeight => Bounds.Height;

    protected override Size MeasureOverride(Size availableSize) =>
        new(double.IsInfinity(availableSize.Width) ? 480 : availableSize.Width, double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        ArgumentNullException.ThrowIfNull(change);
        base.OnPropertyChanged(change);
        if (change.Property != InputProperty)
        {
            return;
        }

        Shape target = Shape.Of(Input);
        if (!_to.IsKnown || !target.IsKnown || _to.Triple != target.Triple || TopLevel.GetTopLevel(this) is not { } top)
        {
            // Nothing to glide from: the first picture, an unusable one, a different number of screens, or not shown.
            _from = target;
            _to = target;
            _phase = 1;
            InvalidateVisual();
            return;
        }

        _from = Shape.Lerp(_from, _to, _phase);
        _to = target;
        bool running = _phase < 1;
        _phase = 0;
        _glideStart = DateTime.UtcNow;
        if (!running)
        {
            top.RequestAnimationFrame(Tick);
        }
    }

    private void Tick(TimeSpan _)
    {
        double t = Math.Clamp((DateTime.UtcNow - _glideStart) / Glide, 0, 1);
        _phase = 1 - Math.Pow(1 - t, 3); // cubic ease-out
        InvalidateVisual();
        if (t < 1 && TopLevel.GetTopLevel(this) is { } top)
        {
            top.RequestAnimationFrame(Tick);
        }
        else
        {
            _phase = 1;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPointerMoved(e);
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

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (_hover >= 0)
        {
            _hover = -1;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        base.Render(context);
        Frame frame = Build();
        if (frame.Screens.Count == 0)
        {
            return;
        }

        using DrawingContext.PushedState clip = context.PushClip(new Rect(Bounds.Size));

        // Floor grid in 10 cm steps from the eye: it gives the picture its scale without a single number.
        double step = frame.Scale * 100;
        if (step >= 8)
        {
            var grid = new Pen(Fill("RsText", 0.045), 1);
            for (double x = frame.Eye.X % step; x < ActualWidth; x += step)
            {
                context.DrawLine(grid, new Point(x, 0), new Point(x, ActualHeight));
            }

            for (double y = frame.Eye.Y % step; y < ActualHeight; y += step)
            {
                context.DrawLine(grid, new Point(0, y), new Point(ActualWidth, y));
            }
        }

        // The cone: light from the eye fading towards the screens; side screens a step darker than the centre.
        double reach = frame.Outline.Max(p => ((Vector)(p - frame.Eye)).Length);
        Color accent = ColorOf("RsAccent");
        var light = new RadialGradientBrush
        {
            Center = new RelativePoint(frame.Eye, RelativeUnit.Absolute),
            GradientOrigin = new RelativePoint(frame.Eye, RelativeUnit.Absolute),
            RadiusX = new RelativeScalar(reach * 1.15, RelativeUnit.Absolute),
            RadiusY = new RelativeScalar(reach * 1.15, RelativeUnit.Absolute),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(87, accent.R, accent.G, accent.B), 0),
                new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1),
            },
        };
        int centre = frame.Screens.Count == 3 ? 1 : 0;
        for (int i = 0; i < frame.Screens.Count; i++)
        {
            using (context.PushOpacity(i == centre ? 1 : 0.55))
            {
                context.DrawGeometry(light, null, Fan(frame.Eye, frame.Screens[i]));
            }
        }

        var edge = new Pen(Fill("RsAccent", 0.6), 1.5);
        context.DrawLine(edge, frame.Eye, frame.Outline[0]);
        context.DrawLine(edge, frame.Eye, frame.Outline[^1]);

        // The screens: bars with round ends, joints where triples meet.
        for (int i = 0; i < frame.Screens.Count; i++)
        {
            var pen = new Pen(BrushOf(i == _hover ? "RsAccentText" : "RsText3"), 5, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
            context.DrawGeometry(null, pen, Polyline(frame.Screens[i]));
        }

        if (frame.Screens.Count == 3)
        {
            var joint = new Pen(BrushOf("RsText3"), 1.5);
            foreach (Point point in new[] { frame.Screens[1][0], frame.Screens[1][^1] })
            {
                context.DrawEllipse(BrushOf("RsDeep"), joint, point, 4, 4);
            }
        }

        // Distance as a dimension line beside the eye's line of sight, the width over the centre screen.
        IReadOnlyList<Point> middle = frame.Screens[centre];
        double span = Math.Abs(middle[^1].X - middle[0].X);
        double dimensionX = frame.Eye.X + Math.Max(44, span * 0.28);
        var dimension = new Pen(Fill("RsText2", 0.6), 1);
        double top = frame.Centre.Y + 8;
        context.DrawLine(dimension, new Point(dimensionX, top), new Point(dimensionX, frame.Eye.Y));
        context.DrawLine(dimension, new Point(dimensionX - 5, top), new Point(dimensionX + 5, top));
        context.DrawLine(dimension, new Point(dimensionX - 5, frame.Eye.Y), new Point(dimensionX + 5, frame.Eye.Y));
        Chip(context, frame.Distance, new Point(dimensionX, (top + frame.Eye.Y) / 2), accent: false);
        Chip(context, frame.Width, new Point((middle.Min(p => p.X) + middle.Max(p => p.X)) / 2, middle.Min(p => p.Y) - 20), accent: false);

        // The angle as one arc at the eye, with its value on a chip above the head.
        const double Radius = 34;
        Point from = frame.Eye + (Unit(frame.Outline[0] - frame.Eye) * Radius);
        Point to = frame.Eye + (Unit(frame.Outline[^1] - frame.Eye) * Radius);
        var arc = new StreamGeometry();
        using (StreamGeometryContext c = arc.Open())
        {
            c.BeginFigure(from, false);
            c.ArcTo(to, new Size(Radius, Radius), 0, frame.TotalDegrees > 180, SweepDirection.Clockwise);
            c.EndFigure(false);
        }

        context.DrawGeometry(null, new Pen(BrushOf("RsAccent"), 2), arc);

        // The head: a soft shadow, the head itself and a small nose for the direction of view.
        context.DrawEllipse(Fill("RsText", 0.05), null, frame.Eye + new Vector(0, 18), 30, 9);
        context.DrawEllipse(BrushOf("RsCard"), new Pen(BrushOf("RsText"), 1.5), frame.Eye + new Vector(0, 4), 10, 10);
        var nose = new StreamGeometry();
        using (StreamGeometryContext c = nose.Open())
        {
            c.BeginFigure(frame.Eye + new Vector(-4.5, -4), true);
            c.LineTo(frame.Eye + new Vector(0, -10));
            c.LineTo(frame.Eye + new Vector(4.5, -4));
            c.EndFigure(true);
        }

        context.DrawGeometry(BrushOf("RsText"), null, nose);
        Chip(context, frame.Total, frame.Eye - new Vector(0, Radius + 20), accent: true);

        // Hover: the screen's own share of the angle next to it.
        if (_hover >= 0 && _hover < frame.Labels.Count && frame.Labels[_hover].Length > 0)
        {
            IReadOnlyList<Point> screen = frame.Screens[_hover];
            Point anchor = screen[screen.Count / 2];
            Chip(context, frame.Labels[_hover], anchor + (Unit(frame.Eye - anchor) * 26), accent: true);
        }

        if (SideViewBox(frame) is { } box)
        {
            SideView(context, frame, box);
        }
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
        double span = Math.Abs(middle[^1].X - middle[0].X);
        double dimensionX = frame.Eye.X + Math.Max(44, span * 0.28);
        StreamGeometry cone = Fan(frame.Eye, frame.Outline);
        Rect[] used =
        [
            new Rect(frame.Eye.X - 34, frame.Eye.Y - 60, 68, 90),
            new Rect(dimensionX - 34, frame.Centre.Y, 68, Math.Max(0, frame.Eye.Y - frame.Centre.Y)),
            new Rect(middle.Min(p => p.X) - 10, middle.Min(p => p.Y) - 34, span + 20, 40),
        ];
        foreach (Rect box in new[] { new Rect(ActualWidth - W - 12, ActualHeight - H - 12, W, H), new Rect(ActualWidth - W - 12, 12, W, H) })
        {
            Rect inflated = box.Inflate(10);
            // shortcut: the cone is tested on an 8 px grid of points (Avalonia has no geometry-against-geometry test);
            // fine for a corner box, revisit if the side view ever overlaps the cone's thin tips.
            bool free = box.Left > 12 && !used.Any(r => r.Intersects(inflated));
            for (double x = inflated.Left; free && x <= inflated.Right; x += 8)
            {
                for (double y = inflated.Top; free && y <= inflated.Bottom; y += 8)
                {
                    free = !cone.FillContains(new Point(x, y));
                }
            }

            if (free)
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
        context.DrawRectangle(BrushOf("RsContent"), new Pen(BrushOf("RsOutline"), 1), box, 8, 8);
        Text(context, Loc.Instance["Fov_SideView"], box.TopLeft + new Vector(10, 6), "RsText2", 11, FontWeight.Normal);

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

        context.DrawGeometry(Fill("RsAccent", 0.18), null, Fan(eye, [topEdge, bottomEdge]));
        var edge = new Pen(Fill("RsAccent", 0.6), 1);
        context.DrawLine(edge, eye, topEdge);
        context.DrawLine(edge, eye, bottomEdge);
        context.DrawLine(new Pen(BrushOf("RsText3"), 4, lineCap: PenLineCap.Round), topEdge, bottomEdge);
        context.DrawEllipse(BrushOf("RsText"), null, eye, 4, 4);
        Text(context, frame.Vertical, new Point(box.Left + 10, box.Bottom - 22), "RsText", 12, FontWeight.SemiBold);
    }

    private static StreamGeometry Fan(Point eye, IReadOnlyList<Point> screen)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(eye, true);
        foreach (Point point in screen)
        {
            context.LineTo(point);
        }

        context.EndFigure(true);
        return geometry;
    }

    private static StreamGeometry Polyline(IReadOnlyList<Point> points)
    {
        var geometry = new StreamGeometry();
        using StreamGeometryContext context = geometry.Open();
        context.BeginFigure(points[0], false);
        for (int i = 1; i < points.Count; i++)
        {
            context.LineTo(points[i]);
        }

        context.EndFigure(false);
        return geometry;
    }

    private static Vector Unit(Vector v) => v.Length > 0 ? v / v.Length : new Vector(0, -1);

    private static bool Near(IReadOnlyList<Point> points, Point position)
    {
        for (int i = 1; i < points.Count; i++)
        {
            Vector segment = points[i] - points[i - 1];
            double length = segment.SquaredLength;
            double t = length <= 0 ? 0 : Math.Clamp(Vector.Dot(position - points[i - 1], segment) / length, 0, 1);
            if (((Vector)(position - (points[i - 1] + (segment * t)))).Length < 10)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A value on a small pill: accent-filled for the result, quiet for dimensions.</summary>
    private void Chip(DrawingContext context, string text, Point centre, bool accent)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        FormattedText formatted = Format(text, accent ? "RsOnAccent" : "RsText", 12, FontWeight.SemiBold);
        double width = formatted.Width + 20;
        const double Height = 22;
        var rect = new Rect(centre.X - (width / 2), centre.Y - (Height / 2), width, Height);
        context.DrawRectangle(BrushOf(accent ? "RsAccent" : "RsCard"), accent ? null : new Pen(BrushOf("RsOutline"), 1), rect, Height / 2, Height / 2);
        context.DrawText(formatted, new Point(rect.Left + 10, rect.Top + ((Height - formatted.Height) / 2)));
    }

    private void Text(DrawingContext context, string text, Point at, string brushKey, double size, FontWeight weight)
    {
        if (!string.IsNullOrEmpty(text))
        {
            context.DrawText(Format(text, brushKey, size, weight), at);
        }
    }

    private FormattedText Format(string text, string brushKey, double size, FontWeight weight) =>
        new(text, Loc.Instance.Culture, FlowDirection.LeftToRight,
            new Typeface(this.TryFindResource("RsFont", ActualThemeVariant, out object? font) && font is FontFamily family ? family : FontFamily.Default, FontStyle.Normal, weight),
            size, BrushOf(brushKey));

    private IBrush BrushOf(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Gray;

    private Color ColorOf(string key) => BrushOf(key) is ISolidColorBrush solid ? solid.Color : Colors.Gray;

    private SolidColorBrush Fill(string key, double opacity) => new(ColorOf(key), opacity);

    /// <summary>The picture for the current moment of the glide, in device-independent pixels.</summary>
    private Frame Build()
    {
        Shape shape = Shape.Lerp(_from, _to, _phase);
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
        /// <summary>Pixels per millimetre.</summary>
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
