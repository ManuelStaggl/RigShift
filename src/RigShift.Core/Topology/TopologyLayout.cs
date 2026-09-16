namespace RigShift.Core.Topology;

/// <summary>State of one display as the topology picture shows it.</summary>
public enum TopologyDisplayState
{
    /// <summary>Part of the desktop.</summary>
    Active,

    /// <summary>Connected, not in use.</summary>
    Off,

    /// <summary>A profile needs it, Windows does not see it.</summary>
    Missing,
}

/// <summary>
/// One display of a topology picture: its desktop rectangle in pixels plus what the picture says about it. Built by the
/// UI from a profile or a snapshot; the picture never edits it – arranging happens in Windows.
/// </summary>
public sealed record TopologyDisplay
{
    /// <summary>Identifies the display for selection, e.g. the target device path.</summary>
    public required string Key { get; init; }

    public required int X { get; init; }

    public required int Y { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    /// <summary>Number as Windows counts displays; null when unknown.</summary>
    public int? Number { get; init; }

    public string Name { get; init; } = string.Empty;

    /// <summary>Mode line under the name, e.g. "3840×2160 @ 165 Hz"; the picture shows "missing" instead for a missing display.</summary>
    public string? Mode { get; init; }

    /// <summary>Full text for a tooltip: name, path, refresh rate, HDR.</summary>
    public string? Details { get; init; }

    public TopologyDisplayState State { get; init; } = TopologyDisplayState.Active;

    public bool IsPrimary { get; init; }

    public bool IsOptional { get; init; }
}

/// <summary>Where one display lands inside the picture, in device-independent pixels of the picture.</summary>
public sealed record TopologyRect(TopologyDisplay Display, double X, double Y, double Width, double Height, bool ShowLabel);

/// <summary>
/// Scales a display arrangement into a picture of a given size: the bounding box of all displays is fitted with one
/// factor (<c>min(sx, sy)</c>) so proportions stay true, then centered. Displays left of or above the primary have
/// negative desktop coordinates; they are shifted so the picture starts at zero.
/// </summary>
public static class TopologyLayout
{
    /// <summary>Below this size a display gets no label – the text would not fit.</summary>
    public const double MinLabelWidth = 24;

    public const double MinLabelHeight = 14;

    /// <param name="gap">Space between neighbouring displays, taken from each side of every rectangle.</param>
    /// <param name="minLabelWidth">A display narrower than this shows no label; defaults to <see cref="MinLabelWidth"/>.</param>
    /// <param name="minLabelHeight">A display lower than this shows no label; defaults to <see cref="MinLabelHeight"/>.</param>
    public static IReadOnlyList<TopologyRect> Arrange(
        IReadOnlyList<TopologyDisplay> displays,
        double width,
        double height,
        double gap,
        double minLabelWidth = MinLabelWidth,
        double minLabelHeight = MinLabelHeight)
    {
        ArgumentNullException.ThrowIfNull(displays);

        var visible = displays.Where(d => d.Width > 0 && d.Height > 0).ToList();
        if (visible.Count == 0 || width <= 0 || height <= 0)
        {
            return [];
        }

        int minX = visible.Min(d => d.X);
        int minY = visible.Min(d => d.Y);
        int maxX = visible.Max(d => d.X + d.Width);
        int maxY = visible.Max(d => d.Y + d.Height);
        double scale = Math.Min(width / (maxX - minX), height / (maxY - minY));
        double offsetX = (width - ((maxX - minX) * scale)) / 2;
        double offsetY = (height - ((maxY - minY) * scale)) / 2;

        var rects = new List<TopologyRect>(visible.Count);
        foreach (TopologyDisplay display in visible)
        {
            double x = offsetX + ((display.X - minX) * scale) + gap;
            double y = offsetY + ((display.Y - minY) * scale) + gap;
            double w = Math.Max(1, (display.Width * scale) - (2 * gap));
            double h = Math.Max(1, (display.Height * scale) - (2 * gap));
            rects.Add(new TopologyRect(display, x, y, w, h, w >= minLabelWidth && h >= minLabelHeight));
        }

        return rects;
    }
}
