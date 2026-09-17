using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using RigShift.App.Localization;
using RigShift.Core.Topology;

namespace RigShift.App.Controls;

/// <summary>The four sizes of the topology picture; each fixes gap, corner radius and whether displays carry labels.</summary>
public enum TopologySize
{
    /// <summary>56 × 24, no labels: tray popup rows, profile choice in a game.</summary>
    XS,

    /// <summary>64 × 28, no labels: master list.</summary>
    S,

    /// <summary>480 × 200 and up, labels: overview, wizard, confirmation.</summary>
    M,

    /// <summary>Fluid, labels, clickable with a selection ring: profile detail.</summary>
    L,
}

/// <summary>
/// The signature picture of RigShift: every display of an arrangement as a rectangle, true to scale, with number,
/// name and mode when there is room. States: active, main display (accent edge), optional (dashed), off (darker),
/// missing (hatched, error edge), selected (dashed accent ring, size L only). The picture is never draggable –
/// arranging happens in Windows.
/// </summary>
public sealed class DisplayTopology : FrameworkElement
{
    public static readonly DependencyProperty DisplaysProperty = DependencyProperty.Register(
        nameof(Displays), typeof(IReadOnlyList<TopologyDisplay>), typeof(DisplayTopology),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsMeasure, OnDisplaysChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(TopologySize), typeof(DisplayTopology),
        new FrameworkPropertyMetadata(TopologySize.M, FrameworkPropertyMetadataOptions.AffectsMeasure, (d, _) => ((DisplayTopology)d).Rebuild()));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(DisplayTopology),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((DisplayTopology)d).UpdateSelection()));

    private static readonly Size[] DefaultSizes = [new(56, 24), new(64, 28), new(480, 200), new(560, 220)];

    private readonly VisualCollection _visuals;
    private readonly List<Tile> _tiles = [];

    public DisplayTopology()
    {
        _visuals = new VisualCollection(this);
        // A method group: the weak event manager only holds the handler's target weakly, a lambda's closure would be collected.
        PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
    }

    public IReadOnlyList<TopologyDisplay>? Displays
    {
        get => (IReadOnlyList<TopologyDisplay>?)GetValue(DisplaysProperty);
        set => SetValue(DisplaysProperty, value);
    }

    public TopologySize Size
    {
        get => (TopologySize)GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    /// <summary>Key of the selected display; only size L shows and changes it.</summary>
    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    protected override int VisualChildrenCount => _visuals.Count;

    private bool HasLabels => Size is TopologySize.M or TopologySize.L;

    private bool IsSelectable => Size == TopologySize.L;

    private double Gap => HasLabels ? 3 : 1;

    private double Radius => Size switch
    {
        TopologySize.XS or TopologySize.S => 2,
        TopologySize.M => 3,
        _ => 4,
    };

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        Size fallback = DefaultSizes[(int)Size];
        var size = new Size(
            double.IsInfinity(availableSize.Width) ? fallback.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? fallback.Height : availableSize.Height);
        foreach (Tile tile in _tiles)
        {
            tile.Root.Measure(size);
        }

        return size;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double minLabel = HasLabels ? 60 : double.MaxValue;
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            _tiles.Select(t => t.Display).ToList(), finalSize.Width, finalSize.Height, Gap, minLabel, HasLabels ? 30 : double.MaxValue);
        for (int i = 0; i < _tiles.Count; i++)
        {
            TopologyRect rect = rects[i];
            var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            _tiles[i].Labels.Visibility = rect.ShowLabel ? Visibility.Visible : Visibility.Collapsed;
            _tiles[i].FitLabels(bounds.Width);
            _tiles[i].Root.Measure(bounds.Size);
            _tiles[i].Root.Arrange(bounds);
        }

        return finalSize;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TopologyAutomationPeer(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || !IsSelectable || _tiles.Count == 0)
        {
            return;
        }

        // Left/Right walk the displays in desktop order, Home/End jump to the ends.
        var ordered = _tiles.OrderBy(t => t.Display.X).ThenBy(t => t.Display.Y).Select(t => t.Display.Key).ToList();
        int current = ordered.IndexOf(SelectedKey ?? string.Empty);
        int next = e.Key switch
        {
            Key.Left or Key.Up => Math.Max(0, current - 1),
            Key.Right or Key.Down => Math.Min(ordered.Count - 1, current + 1),
            Key.Home => 0,
            Key.End => ordered.Count - 1,
            _ => -1,
        };
        if (next >= 0)
        {
            SelectedKey = ordered[next];
            e.Handled = true;
        }
    }

    private static void OnDisplaysChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (DisplayTopology)d;
        if (e.OldValue is INotifyCollectionChanged old)
        {
            CollectionChangedEventManager.RemoveHandler(old, control.OnCollectionChanged);
        }

        if (e.NewValue is INotifyCollectionChanged changed)
        {
            CollectionChangedEventManager.AddHandler(changed, control.OnCollectionChanged);
        }

        control.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _visuals.Clear();
        _tiles.Clear();
        // Same filter as the layout, so tiles and rectangles line up by index.
        foreach (TopologyDisplay display in (Displays ?? []).Where(d => d.Width > 0 && d.Height > 0))
        {
            var tile = new Tile(display, this);
            _tiles.Add(tile);
            _visuals.Add(tile.Root);
        }

        Focusable = IsSelectable && _tiles.Count > 0;
        UpdateSelection();
        UpdateAutomationName();
        InvalidateMeasure();
    }

    private void UpdateSelection()
    {
        foreach (Tile tile in _tiles)
        {
            tile.Ring.Visibility = IsSelectable && tile.Display.Key == SelectedKey ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>"3 displays: XG32UCWG (main display), Left, Right" for screen readers.</summary>
    private void UpdateAutomationName()
    {
        string names = string.Join(", ", _tiles.Select(t =>
            t.Display.IsPrimary ? Loc.Format("Topology_PrimaryName", t.Display.Name) : t.Display.Name));
        AutomationProperties.SetName(this, _tiles.Count == 0
            ? Loc.Instance["Topology_Empty"]
            : Loc.Format("Topology_Description", _tiles.Count, names));
    }

    private void Select(TopologyDisplay display)
    {
        if (!IsSelectable)
        {
            return;
        }

        Focus();
        SelectedKey = display.Key;
    }

    /// <summary>One display: rectangle, selection ring and the two label lines, arranged by the owner.</summary>
    private sealed class Tile
    {
        public Tile(TopologyDisplay display, DisplayTopology owner)
        {
            Display = display;
            bool missing = display.State == TopologyDisplayState.Missing;
            bool off = display.State == TopologyDisplayState.Off;

            var shape = new Rectangle
            {
                RadiusX = owner.Radius,
                RadiusY = owner.Radius,
                StrokeThickness = display.IsPrimary ? 2 : 1,
                SnapsToDevicePixels = true,
            };
            shape.SetResourceReference(Shape.StrokeProperty, missing ? "RigShift.Brush.Error" : display.IsPrimary ? "RigShift.Brush.Accent" : "RigShift.Brush.LineStrong");
            if (display.IsOptional)
            {
                shape.StrokeDashArray = [4, 3];
            }

            if (missing)
            {
                shape.SetResourceReference(Shape.FillProperty, "RigShift.Brush.Hatch");
            }
            else if (!display.IsOptional)
            {
                shape.SetResourceReference(Shape.FillProperty, off ? "RigShift.Brush.DisplayFillOff" : "RigShift.Brush.DisplayFill");
            }

            Ring = new Rectangle
            {
                Margin = new Thickness(-5),
                RadiusX = owner.Radius + 3,
                RadiusY = owner.Radius + 3,
                StrokeThickness = 1,
                StrokeDashArray = [3, 3],
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            Ring.SetResourceReference(Shape.StrokeProperty, "RigShift.Brush.Accent");

            var name = new TextBlock
            {
                Text = display.Number is { } number ? number.ToString(Loc.Instance.Culture) + " · " + display.Name : display.Name,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 0),
            };
            name.SetResourceReference(TextBlock.FontFamilyProperty, "RigShift.Font.Text");
            name.SetResourceReference(TextBlock.ForegroundProperty, off ? "RigShift.Brush.TextSecondary" : "RigShift.Brush.TextPrimary");
            var mode = new TextBlock
            {
                Text = missing ? Loc.Instance["Topology_Missing"] : display.Mode,
                FontSize = 11,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(6, 0, 6, 0),
            };
            mode.SetResourceReference(TextBlock.FontFamilyProperty, "RigShift.Font.Mono");
            mode.SetResourceReference(TextBlock.ForegroundProperty, missing ? "RigShift.Brush.Error" : "RigShift.Brush.TextSecondary");
            Labels = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = owner.HasLabels ? Visibility.Visible : Visibility.Collapsed,
            };
            Labels.Children.Add(name);
            Labels.Children.Add(mode);
            NameLabel = name;
            NameFull = name.Text ?? string.Empty;
            // "1 - Left - CM27X3" -> "1 - Left": a small tile drops the model before it drops letters.
            int lastDot = NameFull.LastIndexOf(" · ", StringComparison.Ordinal);
            NameShort = lastDot > 0 && NameFull.IndexOf(" · ", StringComparison.Ordinal) != lastDot
                ? NameFull[..lastDot]
                : string.Empty;
            ModeLabel = mode;
            ModeFull = mode.Text ?? string.Empty;
            // "1920 x 1080 @ 100 Hz" -> "1920 x 1080": a small tile shows the resolution rather than half a number.
            int at = ModeFull.IndexOf(" @ ", StringComparison.Ordinal);
            ModeShort = at > 0 ? ModeFull[..at] : string.Empty;

            Root = new Grid { Background = Brushes.Transparent };
            Root.Children.Add(shape);
            Root.Children.Add(Labels);
            Root.Children.Add(Ring);
            if (owner.HasLabels && !string.IsNullOrEmpty(display.Details))
            {
                Root.ToolTip = display.Details;
            }

            if (owner.IsSelectable)
            {
                Root.Cursor = Cursors.Hand;
                Root.MouseLeftButtonDown += (_, e) =>
                {
                    owner.Select(display);
                    e.Handled = true;
                };
            }
        }

        public TopologyDisplay Display { get; }

        public Grid Root { get; }

        public Rectangle Ring { get; }

        public StackPanel Labels { get; }

        private TextBlock NameLabel { get; }

        private string NameFull { get; }

        private string NameShort { get; }

        private TextBlock ModeLabel { get; }

        private string ModeFull { get; }

        private string ModeShort { get; }

        /// <summary>Picks the longest form of each label that fits the tile, so none is cut mid-word or mid-number.</summary>
        public void FitLabels(double width)
        {
            Fit(NameLabel, NameFull, NameShort, width);
            Fit(ModeLabel, ModeFull, ModeShort, width);
        }

        private static void Fit(TextBlock label, string full, string shorter, double width)
        {
            if (shorter.Length == 0)
            {
                return;
            }

            label.Text = full;
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (label.DesiredSize.Width > width)
            {
                label.Text = shorter;
            }
        }
    }

    private sealed class TopologyAutomationPeer(DisplayTopology owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(DisplayTopology);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
