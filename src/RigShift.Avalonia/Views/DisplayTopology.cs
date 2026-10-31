using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using RigShift.App.Localization;
using RigShift.Core.Topology;

namespace RigShift.Avalonia.Views;

/// <summary>The three sizes of the topology picture.</summary>
public enum TopologySize
{
    /// <summary>56 × 24, no labels: tray rows, the current-layout line.</summary>
    Tray,

    /// <summary>Labels: overview switch positions.</summary>
    Card,

    /// <summary>Fluid, labels, a display can be selected: profile sheet.</summary>
    Full,
}

/// <summary>
/// Every display of an arrangement as a framed screen true to scale, placed by <see cref="TopologyLayout"/>. Number,
/// name and mode as labels, an accent bezel for the main display; filled = in use, hollow = off, dashed = optional, dashed in the
/// warning colour = missing. In size Full a display is selected by click or arrow keys, and each display is its own
/// focusable element, so Narrator reads its name. The tiles are measured in arrange only, against their own rectangle:
/// the picture never sizes to them, so the layout cannot swing back and forth.
/// </summary>
public sealed class DisplayTopology : Panel
{
    public static readonly StyledProperty<IReadOnlyList<TopologyDisplay>?> DisplaysProperty =
        AvaloniaProperty.Register<DisplayTopology, IReadOnlyList<TopologyDisplay>?>(nameof(Displays));

    public static readonly StyledProperty<TopologySize> SizeProperty =
        AvaloniaProperty.Register<DisplayTopology, TopologySize>(nameof(Size), TopologySize.Card);

    public static readonly StyledProperty<string?> SelectedKeyProperty =
        AvaloniaProperty.Register<DisplayTopology, string?>(nameof(SelectedKey), defaultBindingMode: BindingMode.TwoWay);

    private readonly List<Tile> _tiles = [];

    public DisplayTopology()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent;
        // One tab stop for the whole picture; the arrow keys move inside it.
        KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Once);
        ActualThemeVariantChanged += (_, _) => Rebuild();
    }

    public IReadOnlyList<TopologyDisplay>? Displays
    {
        get => GetValue(DisplaysProperty);
        set => SetValue(DisplaysProperty, value);
    }

    public TopologySize Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    public string? SelectedKey
    {
        get => GetValue(SelectedKeyProperty);
        set => SetValue(SelectedKeyProperty, value);
    }

    private bool HasLabels => Size != TopologySize.Tray;

    private bool IsSelectable => Size == TopologySize.Full;

    private double Gap => Size switch
    {
        TopologySize.Full => 4,
        TopologySize.Card => 4,
        _ => 1,
    };

    private double Radius => Size == TopologySize.Full ? 8 : 4;

    protected override void OnAttachedToLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Loc.Instance.PropertyChanged += OnLanguageChanged;
    }

    protected override void OnDetachedFromLogicalTree(global::Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        base.OnDetachedFromLogicalTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Size fallback = Size switch
        {
            TopologySize.Tray => new Size(56, 24),
            TopologySize.Card => new Size(480, 200),
            _ => new Size(560, 220),
        };

        // Fills what it is given and never reads the tiles, so a tile's new desired size re-measures to the same size;
        // the tiles are measured in ArrangeOverride only.
        return new Size(
            double.IsInfinity(availableSize.Width) ? fallback.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? fallback.Height : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            _tiles.Select(t => t.Display).ToList(), finalSize.Width, finalSize.Height, Gap,
            HasLabels ? 60 : double.MaxValue, HasLabels ? 48 : double.MaxValue);
        for (int i = 0; i < _tiles.Count; i++)
        {
            TopologyRect rect = rects[i];
            var bounds = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            _tiles[i].Layout(bounds.Size, rect.ShowLabel);
            _tiles[i].Root.Measure(bounds.Size);
            _tiles[i].Root.Arrange(bounds);
        }

        return finalSize;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DisplaysProperty || change.Property == SizeProperty)
        {
            Rebuild();
        }
        else if (change.Property == SelectedKeyProperty)
        {
            UpdateSelection();
        }
    }

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
            // Focus follows the selection, so Narrator reads the display it landed on.
            _tiles.First(t => t.Display.Key == SelectedKey).Root.Focus(NavigationMethod.Directional);
            e.Handled = true;
        }
    }

    private void OnLanguageChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        Children.Clear();
        _tiles.Clear();
        // Same filter as the layout, so tiles and rectangles line up by index.
        foreach (TopologyDisplay display in (Displays ?? []).Where(d => d.Width > 0 && d.Height > 0))
        {
            var tile = new Tile(display, this);
            _tiles.Add(tile);
            Children.Add(tile.Root);
        }

        // Size Full is a list of selectable displays, the smaller sizes a picture with one description.
        AutomationProperties.SetIsControlElementOverride(this, true);
        AutomationProperties.SetControlTypeOverride(this, IsSelectable ? AutomationControlType.List : AutomationControlType.Image);
        AutomationProperties.SetName(this, _tiles.Count == 0
            ? Loc.Instance["Topology_Empty"]
            : Loc.Format("Topology_Description", _tiles.Count, string.Join(", ", _tiles.Select(t =>
                t.Display.IsPrimary ? Loc.Format("Topology_PrimaryName", t.Display.Name) : t.Display.Name))));
        UpdateSelection();
        InvalidateArrange();
    }

    private void UpdateSelection()
    {
        foreach (Tile tile in _tiles)
        {
            tile.SetSelected(IsSelectable && tile.Display.Key == SelectedKey);
        }
    }

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as IBrush : null;

    private static TextBlock Line(double size)
    {
        var line = new TextBlock
        {
            FontSize = size,
            LineHeight = size == 13 ? 20 : 16,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            MaxLines = 1,
            IsHitTestVisible = false,
        };
        // The picture or the display's own element says it all; Narrator would read the labels twice.
        AutomationProperties.SetAccessibilityView(line, AccessibilityView.Raw);
        return line;
    }

    /// <summary>One display: frame, inset screen, labels and main-display bezel; shows what fits once the owner knows the rectangle.</summary>
    private sealed class Tile
    {
        private readonly DisplayTopology _owner;
        private readonly Rectangle _frame;
        private readonly StackPanel _labels;
        private readonly TextBlock _name;
        private readonly Run _nameRun;
        private readonly TextBlock? _mode;
        private readonly string _modeFull = string.Empty;
        private readonly Rectangle _screen;
        private readonly Rectangle _bezel;

        public Tile(TopologyDisplay display, DisplayTopology owner)
        {
            Display = display;
            _owner = owner;
            bool missing = display.State == TopologyDisplayState.Missing;
            bool off = display.State == TopologyDisplayState.Off;
            bool dimmed = missing || off || display.IsOptional;
            Root = new Panel { Background = Brushes.Transparent };

            _frame = new Rectangle
            {
                RadiusX = owner.Radius,
                RadiusY = owner.Radius,
                Fill = owner.Brush(!dimmed && owner.Size == TopologySize.Tray ? "RsScreenSurface" : "RsDeep"),
                StrokeThickness = 1,
            };
            if (missing || display.IsOptional)
            {
                _frame.StrokeDashArray = [4, 4];
            }

            Root.Children.Add(_frame);

            _screen = new Rectangle
            {
                RadiusX = 4,
                RadiusY = 4,
                Margin = new Thickness(4, 4, 4, 8),
                Fill = dimmed ? null : owner.Brush("RsScreenSurface"),
                IsHitTestVisible = false,
            };
            Root.Children.Add(_screen);
            _bezel = new Rectangle
            {
                Height = 2,
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Fill = owner.Brush(missing ? "RsWarning" : display.IsPrimary ? "RsAccentText" : "RsScreenEdge"),
                IsHitTestVisible = false,
            };
            Root.Children.Add(_bezel);

            _labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 8, 12, 12), Spacing = 4, IsHitTestVisible = false };
            _name = Line(owner.Size == TopologySize.Full ? 13 : 12);
            _name.FontWeight = FontWeight.SemiBold;
            _name.Foreground = owner.Brush(dimmed ? "RsText2" : "RsText");
            _name.Inlines ??= [];
            _nameRun = new Run(Numbered(display));
            _name.Inlines.Add(_nameRun);
            _labels.Children.Add(_name);
            string? mode = missing ? Loc.Instance["Topology_Missing"] : display.Mode;
            if (!string.IsNullOrEmpty(mode))
            {
                _modeFull = mode;
                _mode = Line(12);
                _mode.Text = mode;
                _mode.Foreground = owner.Brush(missing ? "RsWarning" : "RsText3");
                _labels.Children.Add(_mode);
            }

            Root.Children.Add(_labels);

            if (owner.HasLabels && !string.IsNullOrEmpty(display.Details))
            {
                ToolTip.SetTip(Root, display.Details);
            }

            if (owner.IsSelectable)
            {
                Root.Focusable = true;
                Root.Cursor = new Cursor(StandardCursorType.Hand);
                AutomationProperties.SetIsControlElementOverride(Root, true);
                AutomationProperties.SetControlTypeOverride(Root, AutomationControlType.ListItem);
                AutomationProperties.SetName(Root, SpokenName(display));
                Root.PointerPressed += (_, e) =>
                {
                    Root.Focus(NavigationMethod.Pointer);
                    e.Handled = true;
                };
                // Click, Tab or a screen reader moving focus: the focused display is the selected one.
                Root.GotFocus += (_, _) => owner.SelectedKey = display.Key;
            }

            SetSelected(false);
        }

        public TopologyDisplay Display { get; }

        public Panel Root { get; }

        public void SetSelected(bool selected)
        {
            bool missing = Display.State == TopologyDisplayState.Missing;
            _frame.Stroke = _owner.Brush(selected ? "RsAccentText" : missing ? "RsWarning" : "RsScreenEdge");
            _frame.StrokeThickness = selected ? 2 : 1;
            // The selected display is the one Tab lands on.
            KeyboardNavigation.SetIsTabStop(Root, selected || _owner.SelectedKey is null);
        }

        /// <summary>Labels when the inset has room; a compact bezel remains visible at tray size.</summary>
        public void Layout(Size size, bool showLabels)
        {
            _labels.IsVisible = showLabels;
            _mode?.IsVisible = size.Height >= 72;
            bool miniature = _owner.Size == TopologySize.Tray;
            _screen.IsVisible = !miniature && size.Width >= 24 && size.Height >= 24;
            _bezel.Width = Math.Min(miniature ? 12 : 24, Math.Max(0, size.Width - 8));
            _bezel.Margin = new Thickness(0, 0, 0, miniature ? 2 : 4);
            _bezel.IsVisible = size.Width >= 8 && size.Height >= 8;
            if (!showLabels)
            {
                return;
            }

            // A small display drops the model ("1 · Left · CM27X3" -> "1 · Left") and the rate ("… · 100 Hz") before letters.
            double room = size.Width - _labels.Margin.Left - _labels.Margin.Right;
            string full = Numbered(Display);
            string name = Shorter(full, TextWidth(_name, full) > room);
            if (_nameRun.Text != name)
            {
                _nameRun.Text = name;
            }

            if (_mode is not null)
            {
                string mode = Shorter(_modeFull, TextWidth(_mode, _modeFull) > room);
                if (_mode.Text != mode)
                {
                    _mode.Text = mode;
                }
            }
        }

        /// <summary>The text without its last " · " part when it does not fit and has one.</summary>
        private static string Shorter(string text, bool tooWide)
        {
            int cut = text.LastIndexOf(" · ", StringComparison.Ordinal);
            return tooWide && cut > 0 ? text[..cut] : text;
        }

        // Measured on the side: setting both forms in turn would re-measure on every pass.
        private static double TextWidth(TextBlock block, string text)
        {
            using var layout = new TextLayout(text, new Typeface(block.FontFamily, block.FontStyle, block.FontWeight), block.FontSize, null);
            return layout.Width;
        }

        private static string Numbered(TopologyDisplay display) =>
            display.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) + " · " + display.Name : display.Name;

        /// <summary>"1 · Left, main display, optional, 2560 × 1440 · 165 Hz" for Narrator.</summary>
        private static string SpokenName(TopologyDisplay display)
        {
            List<string> parts = [Numbered(display)];
            if (display.IsPrimary)
            {
                parts.Add(Loc.Instance["Profile_Primary"]);
            }

            parts.Add(display.State switch
            {
                TopologyDisplayState.Missing => Loc.Instance["Topology_Missing"],
                TopologyDisplayState.Off => Loc.Instance["Topology_Off"],
                _ => display.Mode ?? string.Empty,
            });
            if (display.IsOptional)
            {
                parts.Add(Loc.Instance["Profile_Optional"]);
            }

            return string.Join(", ", parts.Where(p => p.Length > 0));
        }
    }
}
