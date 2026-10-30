using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RigShift.Core.Topology;
using RigShift.UI.Controls;
using Windows.Graphics;

namespace RigShift.UI.Debug;

/// <summary>
/// Debug only (<c>RIGSHIFT_PREVIEW_TOPOLOGY=1</c>): every given arrangement plus mirrored, Surround and missing ones in
/// all three sizes, for screenshots.
/// </summary>
public sealed partial class TopologyGalleryWindow : Window
{
    public TopologyGalleryWindow(IEnumerable<(string Name, IReadOnlyList<TopologyDisplay> Displays)> arrangements)
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1240, 1400));
        foreach ((string name, IReadOnlyList<TopologyDisplay> displays) in Samples().Concat(arrangements))
        {
            Rows.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
            var sizes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 24 };
            sizes.Children.Add(new DisplayTopology { Size = TopologySize.Tray, Displays = displays, VerticalAlignment = VerticalAlignment.Top });
            sizes.Children.Add(new DisplayTopology { Size = TopologySize.Card, Displays = displays, Width = 480, Height = 200 });
            sizes.Children.Add(new DisplayTopology { Size = TopologySize.Full, Displays = displays, Width = 560, Height = 220, SelectedKey = displays.Count > 0 ? displays[0].Key : null });
            Rows.Children.Add(sizes);
        }
    }

    private static IEnumerable<(string, IReadOnlyList<TopologyDisplay>)> Samples()
    {
        yield return ("Mirrored", [
            Display("a", 0, 0, 2560, 1440, 1, "Left · CM27X3", "Mirrored", primary: true),
            Display("b", 0, 0, 2560, 1440, 2, "Right · CM27X3", "Mirrored"),
            Display("c", 2560, 0, 3840, 2160, 3, "XG32UCWG", "3840 × 2160 · 240 Hz"),
        ]);
        yield return ("Surround", [
            Display("s", 0, 0, 7680, 1440, 1, "Surround", "7680 × 1440 · 165 Hz", primary: true),
        ]);
        yield return ("Missing, optional, off, portrait", [
            Display("m", 0, 0, 5120, 1440, 1, "Odyssey G93SC", null, primary: true, state: TopologyDisplayState.Missing),
            Display("o", 5120, 0, 1440, 2560, 2, "Portrait · CM27X3", "1440 × 2560 · 60 Hz", optional: true),
            Display("f", -1920, 360, 1920, 1080, 3, "TV", "1920 × 1080 · 60 Hz", state: TopologyDisplayState.Off),
        ]);
    }

    private static TopologyDisplay Display(string key, int x, int y, int width, int height, int number, string name, string? mode,
        bool primary = false, bool optional = false, TopologyDisplayState state = TopologyDisplayState.Active) => new()
    {
        Key = key,
        X = x,
        Y = y,
        Width = width,
        Height = height,
        Number = number,
        Name = name,
        Mode = mode,
        IsPrimary = primary,
        IsOptional = optional,
        State = state,
    };
}
