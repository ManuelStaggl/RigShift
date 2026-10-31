using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RigShift.App.Localization;
using RigShift.Core.Topology;

namespace RigShift.Avalonia.Views;

/// <summary>The three sizes of the topology picture.</summary>
public enum TopologySize
{
    /// <summary>56 × 24, no labels: the current-layout line.</summary>
    Tray,

    /// <summary>Labels: overview switch positions.</summary>
    Card,

    /// <summary>Fluid, labels, a display can be selected: profile sheet.</summary>
    Full,
}

/// <summary>
/// Port of the WinUI DisplayTopology: plain rectangles true to scale on a canvas, from <see cref="TopologyLayout"/>,
/// placed once per change of arrangement, size, selection or theme. Number, name and mode as labels, a star for the main
/// display, dashed for optional or missing. Neutral greys; the selected display's outline in the accent line colour.
/// </summary>
public sealed class DisplayTopology : Canvas
{
    public static readonly StyledProperty<IReadOnlyList<TopologyDisplay>?> DisplaysProperty =
        AvaloniaProperty.Register<DisplayTopology, IReadOnlyList<TopologyDisplay>?>(nameof(Displays));

    public static readonly StyledProperty<TopologySize> SizeProperty =
        AvaloniaProperty.Register<DisplayTopology, TopologySize>(nameof(Size), TopologySize.Card);

    public static readonly StyledProperty<string?> SelectedKeyProperty =
        AvaloniaProperty.Register<DisplayTopology, string?>(nameof(SelectedKey), defaultBindingMode: BindingMode.TwoWay);

    public DisplayTopology()
    {
        ClipToBounds = false;
        Background = Brushes.Transparent;
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
        TopologySize.Full => 6,
        TopologySize.Card => 4,
        _ => 1,
    };

    private double Radius => Size == TopologySize.Tray ? 2 : 4;

    protected override Size MeasureOverride(Size availableSize)
    {
        base.MeasureOverride(availableSize);
        Size fallback = Size switch
        {
            TopologySize.Tray => new Size(56, 24),
            TopologySize.Card => new Size(480, 200),
            _ => new Size(560, 220),
        };

        // Fills what it is given and never sizes to its rectangles.
        return new Size(
            double.IsInfinity(availableSize.Width) ? fallback.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? fallback.Height : availableSize.Height);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DisplaysProperty || change.Property == SizeProperty || change.Property == SelectedKeyProperty
            || change.Property == BoundsProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        IReadOnlyList<TopologyDisplay> displays = Displays ?? [];
        Focusable = IsSelectable && displays.Count > 0;
        AutomationProperties.SetName(this, displays.Count == 0
            ? Loc.Instance["Topology_Empty"]
            : Loc.Format("Topology_Description", displays.Count, string.Join(", ", displays.Select(d =>
                d.IsPrimary ? Loc.Format("Topology_PrimaryName", d.Name) : d.Name))));
        if (Bounds.Width <= 0 || Bounds.Height <= 0)
        {
            return;
        }

        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            displays, Bounds.Width, Bounds.Height, Gap, HasLabels ? 60 : double.MaxValue, HasLabels ? 30 : double.MaxValue);
        foreach (TopologyRect rect in rects)
        {
            Panel tile = Tile(rect);
            SetLeft(tile, rect.X);
            SetTop(tile, rect.Y);
            Children.Add(tile);
        }
    }

    private Panel Tile(TopologyRect rect)
    {
        TopologyDisplay display = rect.Display;
        bool missing = display.State == TopologyDisplayState.Missing;
        bool off = display.State == TopologyDisplayState.Off;
        bool selected = IsSelectable && display.Key == SelectedKey;
        var tile = new Panel { Width = rect.Width, Height = rect.Height, Background = Brushes.Transparent };

        // Filled = in use, hollow = off, dashed = optional, dashed in the warning colour = missing.
        var frame = new Rectangle
        {
            RadiusX = Radius,
            RadiusY = Radius,
            Fill = missing || off || display.IsOptional ? null : Brush("RsTileFill"),
            Stroke = Brush(selected ? "RsAccentText" : missing ? "RsWarning" : "RsTileStroke"),
            StrokeThickness = selected ? 2 : 1,
        };
        if (missing || display.IsOptional)
        {
            frame.StrokeDashArray = [4, 3];
        }

        tile.Children.Add(frame);

        if (rect.ShowLabel)
        {
            tile.Children.Add(Labels(display, rect.Height, missing || off || display.IsOptional));
            if (!string.IsNullOrEmpty(display.Details))
            {
                ToolTip.SetTip(tile, display.Details);
            }
        }
        else if (display.IsPrimary && rect.Width >= 8 && rect.Height >= 8)
        {
            // Without labels a dot marks the main display.
            tile.Children.Add(new Ellipse
            {
                Width = 4,
                Height = 4,
                Margin = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Fill = Brush("RsAccent"),
            });
        }

        if (IsSelectable)
        {
            tile.Cursor = new Cursor(StandardCursorType.Hand);
            tile.PointerPressed += (_, e) =>
            {
                Focus(NavigationMethod.Pointer);
                SelectedKey = display.Key;
                e.Handled = true;
            };
        }

        return tile;
    }

    /// <summary>"★ 1 · Desk" and, when there is height for it, the mode or "missing"; both lines trim instead of overlapping.</summary>
    private StackPanel Labels(TopologyDisplay display, double height, bool dimmed)
    {
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0), Spacing = 1 };
        TextBlock name = Line(Size == TopologySize.Full ? 14 : 12, dimmed ? "RsText2" : "RsText");
        name.FontWeight = FontWeight.SemiBold;
        name.Inlines ??= [];
        if (display.IsPrimary)
        {
            name.Inlines.Add(new Run("★ ") { Foreground = Brush("RsAccentText") });
        }

        string number = display.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) + " · " : string.Empty;
        name.Inlines.Add(new Run(number + display.Name));
        labels.Children.Add(name);

        bool isMissing = display.State == TopologyDisplayState.Missing;
        string? mode = isMissing ? Loc.Instance["Topology_Missing"] : display.Mode;
        if (!string.IsNullOrEmpty(mode) && height >= 48)
        {
            TextBlock line = Line(12, isMissing ? "RsWarning" : "RsText3");
            line.Text = mode;
            labels.Children.Add(line);
        }

        return labels;
    }

    private TextBlock Line(double size, string foreground) => new()
    {
        FontSize = size,
        Foreground = Brush(foreground),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        MaxLines = 1,
    };

    private IBrush? Brush(string key) => this.TryFindResource(key, ActualThemeVariant, out object? value) ? value as IBrush : null;

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        IReadOnlyList<TopologyDisplay> displays = Displays ?? [];
        if (!IsSelectable || displays.Count == 0)
        {
            return;
        }

        // Left/Right walk the displays in desktop order, Home/End jump to the ends.
        var ordered = displays.OrderBy(d => d.X).ThenBy(d => d.Y).Select(d => d.Key).ToList();
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
}
