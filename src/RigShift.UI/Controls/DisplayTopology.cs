using System.ComponentModel;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RigShift.App.Localization;
using RigShift.Core.Topology;
using Windows.Foundation;
using Windows.System;

namespace RigShift.UI.Controls;

/// <summary>The three sizes of the topology picture.</summary>
public enum TopologySize
{
    /// <summary>56 × 24, no labels: tray popup rows.</summary>
    Tray,

    /// <summary>480 × 200 and up, labels: overview, wizard, confirmation.</summary>
    Card,

    /// <summary>Fluid, labels, a display can be selected: profile detail.</summary>
    Full,
}

/// <summary>
/// A display arrangement true to scale: plain rectangles on a canvas with number, name and mode and a star for the main
/// display. The rectangles come from <see cref="TopologyLayout"/>
/// and are placed once per change of arrangement, size, selection, language or theme; nothing measures them back.
/// Arranging happens in Windows, so the picture is never draggable.
/// </summary>
public sealed partial class DisplayTopology : UserControl
{
    public static readonly DependencyProperty DisplaysProperty = DependencyProperty.Register(
        nameof(Displays), typeof(IReadOnlyList<TopologyDisplay>), typeof(DisplayTopology), new PropertyMetadata(null, OnChanged));

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(TopologySize), typeof(DisplayTopology), new PropertyMetadata(TopologySize.Card, OnChanged));

    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(DisplayTopology), new PropertyMetadata(null, OnChanged));

    private const string StarGlyph = "";
    private const string WarningGlyph = "";

    private readonly Canvas _canvas = new();

    public DisplayTopology()
    {
        Content = _canvas;
        UseSystemFocusVisuals = true;
        SizeChanged += (_, _) => Rebuild();
        ActualThemeChanged += (_, _) => Rebuild();
        Loaded += (_, _) => Loc.Instance.PropertyChanged += OnLanguageChanged;
        Unloaded += (_, _) => Loc.Instance.PropertyChanged -= OnLanguageChanged;
        KeyDown += OnKeyDown;
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

    /// <summary>Key of the selected display; only <see cref="TopologySize.Full"/> shows and changes it.</summary>
    public string? SelectedKey
    {
        get => (string?)GetValue(SelectedKeyProperty);
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

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((DisplayTopology)d).Rebuild();

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => DispatcherQueue.TryEnqueue(Rebuild);

    private void Rebuild()
    {
        _canvas.Children.Clear();
        IReadOnlyList<TopologyDisplay> displays = Displays ?? [];
        IsTabStop = IsSelectable && displays.Count > 0;
        AutomationProperties.SetName(this, displays.Count == 0
            ? Loc.Instance["Topology_Empty"]
            : Loc.Format("Topology_Description", displays.Count, string.Join(", ", displays.Select(d =>
                d.IsPrimary ? Loc.Format("Topology_PrimaryName", d.Name) : d.Name))));

        // Room for a name line, and for the mode line under it.
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            displays, ActualWidth, ActualHeight, Gap, HasLabels ? 60 : double.MaxValue, HasLabels ? 30 : double.MaxValue);
        foreach (TopologyRect rect in rects)
        {
            Grid tile = Tile(rect);
            Canvas.SetLeft(tile, rect.X);
            Canvas.SetTop(tile, rect.Y);
            _canvas.Children.Add(tile);
        }
    }

    private Grid Tile(TopologyRect rect)
    {
        TopologyDisplay display = rect.Display;
        bool missing = display.State == TopologyDisplayState.Missing;
        bool off = display.State == TopologyDisplayState.Off;
        bool selected = IsSelectable && display.Key == SelectedKey;
        var tile = new Grid { Width = rect.Width, Height = rect.Height };

        // Filled = in use, hollow = off, dashed = optional, dashed in the caution colour = missing.
        var frame = new Rectangle
        {
            RadiusX = Radius,
            RadiusY = Radius,
            Fill = missing || off || display.IsOptional ? null : Brush("ControlFillColorDefaultBrush"),
            Stroke = Brush(selected ? "AccentFillColorDefaultBrush" : missing ? "SystemFillColorCautionBrush" : "ControlStrongStrokeColorDefaultBrush"),
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
                ToolTipService.SetToolTip(tile, display.Details);
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
                Fill = Brush("AccentFillColorDefaultBrush"),
            });
        }

        if (IsSelectable)
        {
            tile.Tapped += (_, e) =>
            {
                Focus(FocusState.Pointer);
                SelectedKey = display.Key;
                e.Handled = true;
            };
        }

        return tile;
    }

    /// <summary>"★ 1 · Desk" and, when there is height for it, the mode or "missing"; both lines trim instead of overlapping.</summary>
    private StackPanel Labels(TopologyDisplay display, double height, bool dimmed)
    {
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(6, 0, 6, 0) };
        TextBlock name = Line(Size == TopologySize.Full ? 14 : 13, dimmed ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush");
        name.FontWeight = FontWeights.SemiBold;
        if (display.IsPrimary)
        {
            name.Inlines.Add(new Run { Text = StarGlyph + " ", FontFamily = Symbols, Foreground = Brush("AccentTextFillColorPrimaryBrush") });
        }

        string number = display.Number is { } n ? n.ToString(CultureInfo.InvariantCulture) + " · " : string.Empty;
        name.Inlines.Add(new Run { Text = number + display.Name });
        labels.Children.Add(name);

        bool isMissing = display.State == TopologyDisplayState.Missing;
        string? mode = isMissing ? Loc.Instance["Topology_Missing"] : display.Mode;
        if (!string.IsNullOrEmpty(mode) && height >= 48)
        {
            TextBlock line = Line(12, "TextFillColorSecondaryBrush");
            if (isMissing)
            {
                line.Inlines.Add(new Run { Text = WarningGlyph + " ", FontFamily = Symbols, Foreground = Brush("SystemFillColorCautionBrush") });
            }

            line.Inlines.Add(new Run { Text = mode });
            labels.Children.Add(line);
        }

        return labels;
    }

    private static TextBlock Line(double size, string foreground) => new()
    {
        FontSize = size,
        Foreground = Brush(foreground),
        TextAlignment = TextAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        MaxLines = 1,
    };

    private static FontFamily Symbols => (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];

    private static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
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
            VirtualKey.Left or VirtualKey.Up => Math.Max(0, current - 1),
            VirtualKey.Right or VirtualKey.Down => Math.Min(ordered.Count - 1, current + 1),
            VirtualKey.Home => 0,
            VirtualKey.End => ordered.Count - 1,
            _ => -1,
        };
        if (next >= 0)
        {
            SelectedKey = ordered[next];
            e.Handled = true;
        }
    }
}
