using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
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
/// The signature picture of RigShift: every display of an arrangement as a device, true to scale – bezel, screen, a
/// stand in the large sizes, a round number badge like the Windows display settings, a star badge for the main
/// display, name and mode when there is room. States stay apart: selected = accent edge with a soft glow (size L
/// only), hover = lighter screen, optional = dashed outline, off = darker screen, missing = grey screen, faint hatch
/// and a warning symbol. When the arrangement changes, the displays glide into their new places. The picture is never
/// draggable – arranging happens in Windows.
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

    public static readonly DependencyProperty ShowLabelsProperty = DependencyProperty.Register(
        nameof(ShowLabels), typeof(bool), typeof(DisplayTopology),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsArrange));

    private static readonly Size[] DefaultSizes = [new(56, 24), new(64, 28), new(480, 200), new(560, 220)];

    private readonly VisualCollection _visuals;
    private readonly List<Tile> _tiles = [];

    // Where each display stood last time, by key; a rebuild glides from there instead of jumping.
    private readonly Dictionary<string, Rect> _placed = [];
    private bool _glide;

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

    /// <summary>False keeps a large picture to devices and numbers, e.g. in the small tiles of the tray.</summary>
    public bool ShowLabels
    {
        get => (bool)GetValue(ShowLabelsProperty);
        set => SetValue(ShowLabelsProperty, value);
    }

    protected override int VisualChildrenCount => _visuals.Count;

    private bool HasLabels => Size is TopologySize.M or TopologySize.L;

    private bool IsSelectable => Size == TopologySize.L;

    private double Gap => Size switch
    {
        TopologySize.L => 6,
        TopologySize.M => 4,
        _ => 1,
    };

    private double Radius => Size switch
    {
        TopologySize.XS or TopologySize.S => 2,
        TopologySize.M => 5,
        _ => 7,
    };

    private double Bezel => Size switch
    {
        TopologySize.XS or TopologySize.S => 1.5,
        TopologySize.M => 3,
        _ => 4,
    };

    /// <summary>Room under the displays for their stands (sizes M and L).</summary>
    private double StandHeight => Size switch
    {
        TopologySize.L => 16,
        TopologySize.M => 10,
        _ => 0,
    };

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        Size fallback = DefaultSizes[(int)Size];
        // The tiles are measured in ArrangeOverride, against their own bounds only.
        return new Size(
            double.IsInfinity(availableSize.Width) ? fallback.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? fallback.Height : availableSize.Height);
    }

    /// <summary>
    /// The picture places its tiles itself and never sizes to them. Passing a tile's new desired size up would
    /// re-measure the picture, re-arrange the tile and flip the size back – a layout loop that never settles.
    /// </summary>
    protected override void OnChildDesiredSizeChanged(UIElement child)
    {
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double minLabel = HasLabels ? 60 : double.MaxValue;
        // The stands take the bottom strip; the picture stays centered in the rest.
        double stand = Math.Min(StandHeight, finalSize.Height * 0.15);
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            _tiles.Select(t => t.Display).ToList(), finalSize.Width, Math.Max(finalSize.Height - stand, 1), Gap, minLabel, HasLabels ? 30 : double.MaxValue);
        bool glide = _glide && Motion.Enabled && _placed.Count > 0;
        _glide = false;
        var placed = new Dictionary<string, Rect>();
        for (int i = 0; i < _tiles.Count; i++)
        {
            TopologyRect rect = rects[i];
            var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            Tile tile = _tiles[i];
            tile.Layout(bounds.Size, rect.ShowLabel && ShowLabels);
            tile.Root.Measure(bounds.Size);
            tile.Root.Arrange(bounds);
            if (glide)
            {
                tile.GlideFrom(_placed.TryGetValue(tile.Display.Key, out Rect from) ? from : (Rect?)null, bounds);
            }

            placed[tile.Display.Key] = bounds;
        }

        _placed.Clear();
        foreach ((string key, Rect bounds) in placed)
        {
            _placed[key] = bounds;
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

        control._glide = true;
        control.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _glide = true;
        Rebuild();
    }

    /// <summary>The language is app-wide and may change on any thread; the control only on its own.</summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (CheckAccess())
        {
            Rebuild();
        }
        else
        {
            Dispatcher.BeginInvoke(Rebuild);
        }
    }

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
            tile.SetSelected(IsSelectable && tile.Display.Key == SelectedKey);
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

    private static TextBlock Text(double size, FontWeight weight, string foreground)
    {
        var text = new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            IsHitTestVisible = false,
        };
        Typography.SetNumeralAlignment(text, FontNumeralAlignment.Tabular);
        text.SetResourceReference(TextBlock.FontFamilyProperty, "RigShift.Font.Text");
        text.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        return text;
    }

    /// <summary>One display: device shape, glow, badges and the two label lines, arranged by the owner.</summary>
    private sealed class Tile
    {
        private readonly bool _large;
        private readonly DisplayShape _shape;
        private readonly Rectangle _glow;
        private readonly StackPanel _badges;
        private readonly Ellipse _primaryDot;
        private readonly TextBlock _number;
        private readonly TextBlock _nameLabel;
        private readonly TextBlock _modeLabel;
        private readonly string _nameFull;
        private readonly string _nameShort;
        private readonly string _modeFull;
        private readonly string _modeShort;
        private readonly TranslateTransform _shift = new();
        private readonly ScaleTransform _scale = new();

        public Tile(TopologyDisplay display, DisplayTopology owner)
        {
            Display = display;
            bool missing = display.State == TopologyDisplayState.Missing;
            bool off = display.State == TopologyDisplayState.Off;
            bool large = owner.HasLabels;
            _large = large;

            _shape = new DisplayShape
            {
                Radius = owner.Radius,
                Bezel = owner.Bezel,
                StandHeight = display.IsOptional || off ? 0 : owner.StandHeight,
                IsOptional = display.IsOptional,
                IsOff = off,
                IsMissing = missing,
                ShowGloss = large,
            };

            _glow = new Rectangle
            {
                Margin = new Thickness(-2),
                RadiusX = owner.Radius + 2,
                RadiusY = owner.Radius + 2,
                StrokeThickness = 5,
                Opacity = 0,
                IsHitTestVisible = false,
                Effect = new BlurEffect { Radius = 10 },
            };
            _glow.SetResourceReference(Shape.StrokeProperty, "RigShift.Brush.Accent");

            // Badges, top left: number in a disc like the Windows display settings, then a star for the main display.
            _badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(owner.Bezel + 6),
                IsHitTestVisible = false,
            };
            double disc = owner.Size == TopologySize.L ? 20 : 16;
            if (display.Number is { } number)
            {
                // A disc for one digit that grows into a pill: a remote session numbers its display in the hundreds.
                TextBlock digit = Text(disc > 16 ? 11 : 10, FontWeights.SemiBold, "RigShift.Brush.TextPrimary");
                digit.Text = number.ToString(CultureInfo.InvariantCulture);
                digit.TextTrimming = TextTrimming.None;
                digit.VerticalAlignment = VerticalAlignment.Center;
                digit.HorizontalAlignment = HorizontalAlignment.Center;
                var numberBadge = new Border
                {
                    MinWidth = disc,
                    Height = disc,
                    CornerRadius = new CornerRadius(disc / 2),
                    Padding = new Thickness(number >= 10 ? 5 : 0, 0, number >= 10 ? 5 : 0, 0),
                    Margin = new Thickness(0, 0, 4, 0),
                    Child = digit,
                };
                numberBadge.SetResourceReference(Border.BackgroundProperty, "RigShift.Brush.HoverOverlay");
                _badges.Children.Add(numberBadge);
            }

            if (display.IsPrimary)
            {
                var star = new Grid { Width = disc, Height = disc };
                var back = new Ellipse();
                back.SetResourceReference(Shape.FillProperty, "RigShift.Brush.Accent");
                star.Children.Add(back);
                var glyph = new Path
                {
                    Data = Geometry.Parse("M12 2l2.9 6.6 7.1.6-5.4 4.7 1.6 7.1L12 17.3 5.8 21l1.6-7.1L2 9.2l7.1-.6z"),
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(disc * 0.24),
                };
                glyph.SetResourceReference(Shape.FillProperty, "RigShift.Brush.OnAccent");
                star.Children.Add(glyph);
                _badges.Children.Add(star);
            }

            // Small sizes only mark the main display, with a dot.
            _primaryDot = new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(large ? owner.Bezel + 2 : 3),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
                Visibility = display.IsPrimary && owner.Size == TopologySize.S ? Visibility.Visible : Visibility.Collapsed,
            };
            _primaryDot.SetResourceReference(Shape.FillProperty, "RigShift.Brush.Accent");

            // The number alone, centered, when a display in a large picture is too small for its labels.
            _number = Text(11, FontWeights.SemiBold, "RigShift.Brush.TextPrimary");
            _number.Text = display.Number?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
            _number.VerticalAlignment = VerticalAlignment.Center;

            _nameLabel = Text(owner.Size == TopologySize.L ? 14 : 13, FontWeights.SemiBold, off || missing || display.IsOptional ? "RigShift.Brush.TextSecondary" : "RigShift.Brush.TextPrimary");
            _nameLabel.Margin = new Thickness(8, 0, 8, 0);
            _nameLabel.Text = display.Name;
            _modeLabel = Text(12, FontWeights.Normal, "RigShift.Brush.TextSecondary");
            _modeLabel.Margin = new Thickness(8, 2, 8, 0);
            _modeLabel.Text = missing ? Loc.Instance["Topology_Missing"] : display.Mode ?? string.Empty;

            Labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
            if (missing)
            {
                var warn = new Wpf.Ui.Controls.SymbolIcon
                {
                    Symbol = Wpf.Ui.Controls.SymbolRegular.Warning16,
                    FontSize = 16,
                    Margin = new Thickness(0, 0, 0, 4),
                    HorizontalAlignment = HorizontalAlignment.Center,
                };
                warn.SetResourceReference(Control.ForegroundProperty, "RigShift.Brush.Warn");
                Labels.Children.Add(warn);
            }

            Labels.Children.Add(_nameLabel);
            Labels.Children.Add(_modeLabel);

            _nameFull = display.Name;
            // "Left · CM27X3" -> "Left": a small display drops the model before it drops letters.
            int lastDot = _nameFull.LastIndexOf(" · ", StringComparison.Ordinal);
            _nameShort = lastDot > 0 ? _nameFull[..lastDot] : string.Empty;
            _modeFull = _modeLabel.Text ?? string.Empty;
            // "1920 × 1080 · 100 Hz" -> "1920 × 1080": a small display shows the resolution rather than half a number.
            int rate = _modeFull.LastIndexOf(" · ", StringComparison.Ordinal);
            _modeShort = rate > 0 ? _modeFull[..rate] : string.Empty;

            Root = new Grid { Background = Brushes.Transparent };
            Root.Children.Add(_glow);
            Root.Children.Add(_shape);
            Root.Children.Add(_primaryDot);
            Root.Children.Add(_number);
            Root.Children.Add(Labels);
            Root.Children.Add(_badges);
            Root.RenderTransform = new TransformGroup { Children = { _scale, _shift } };
            if (large && !string.IsNullOrEmpty(display.Details))
            {
                Root.ToolTip = display.Details;
            }

            if (owner.IsSelectable)
            {
                Root.Cursor = Cursors.Hand;
                Root.MouseEnter += (_, _) => _shape.IsHovered = true;
                Root.MouseLeave += (_, _) => _shape.IsHovered = false;
                Root.MouseLeftButtonDown += (_, e) =>
                {
                    owner.Select(display);
                    e.Handled = true;
                };
            }
        }

        public TopologyDisplay Display { get; }

        public Grid Root { get; }

        public StackPanel Labels { get; }

        public void SetSelected(bool selected)
        {
            if (_shape.IsSelected == selected)
            {
                return;
            }

            _shape.IsSelected = selected;
            _glow.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(selected ? 0.55 : 0, Motion.Base));
        }

        /// <summary>Shows what fits: labels and badges, only the number, or nothing but the device.</summary>
        public void Layout(Size size, bool showLabels)
        {
            // Badges need a corner of their own; on a small display the number goes back in front of the name.
            bool badges = showLabels && size.Width >= 90 && size.Height >= 70;
            string prefix = !badges && Display.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) + " · " : string.Empty;
            Labels.Visibility = showLabels ? Visibility.Visible : Visibility.Collapsed;
            _badges.Visibility = badges ? Visibility.Visible : Visibility.Collapsed;
            if (_large)
            {
                _primaryDot.Visibility = Display.IsPrimary && !badges ? Visibility.Visible : Visibility.Collapsed;
            }
            _number.Visibility = _large && !showLabels && _number.Text.Length > 0 && size.Width >= 14 && size.Height >= 14
                ? Visibility.Visible : Visibility.Collapsed;
            Fit(_nameLabel, prefix + _nameFull, _nameShort.Length > 0 ? prefix + _nameShort : string.Empty, size.Width);
            Fit(_modeLabel, _modeFull, _modeShort, size.Width);
        }

        /// <summary>Glides from where the display stood before (or fades in when it is new).</summary>
        public void GlideFrom(Rect? from, Rect to)
        {
            var duration = Motion.Layout;
            if (from is not { } old)
            {
                Root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, duration));
                return;
            }

            if (old == to || to.Width < 1 || to.Height < 1)
            {
                return;
            }

            IEasingFunction ease = Motion.Standard;
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(old.Width / to.Width, 1, duration) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(old.Height / to.Height, 1, duration) { EasingFunction = ease });
            _shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(old.X - to.X, 0, duration) { EasingFunction = ease });
            _shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(old.Y - to.Y, 0, duration) { EasingFunction = ease });
        }

        /// <summary>
        /// Sets the longest form that fits. The width is measured on the side: assigning both forms in turn would
        /// invalidate the layout on every pass and never settle.
        /// </summary>
        private static void Fit(TextBlock label, string full, string shorter, double width)
        {
            string choice = shorter.Length > 0 && TextWidth(label, full) > width ? shorter : full;
            if (!string.Equals(label.Text, choice, StringComparison.Ordinal))
            {
                label.Text = choice;
            }
        }

        private static double TextWidth(TextBlock label, string text) =>
            new FormattedText(
                text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(label.FontFamily, label.FontStyle, label.FontWeight, label.FontStretch),
                label.FontSize,
                Brushes.Black,
                VisualTreeHelper.GetDpi(label).PixelsPerDip).Width + label.Margin.Left + label.Margin.Right;
    }

    private sealed class TopologyAutomationPeer(DisplayTopology owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

        protected override string GetClassNameCore() => nameof(DisplayTopology);

        protected override bool IsControlElementCore() => true;

        protected override bool IsContentElementCore() => true;
    }
}
