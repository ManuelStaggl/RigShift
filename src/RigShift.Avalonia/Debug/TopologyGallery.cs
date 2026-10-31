using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using RigShift.Avalonia.Views;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Avalonia.Debug;

/// <summary>
/// Debug only (<c>RIGSHIFT_PREVIEW_TOPOLOGY=1</c>, or a .png path to also save it there): mirrored, Surround, rotated,
/// missing, optional and off arrangements plus every profile's own, each in all three sizes.
/// </summary>
internal static class TopologyGallery
{
    public static void Show(IEnumerable<(string Name, IReadOnlyList<TopologyDisplay> Displays)> arrangements, string? pngPath)
    {
        var rows = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        foreach ((string name, IReadOnlyList<TopologyDisplay> displays) in Samples().Concat(arrangements))
        {
            rows.Children.Add(new TextBlock { Text = name, Classes = { "strong" } });
            rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 24,
                Children =
                {
                    new DisplayTopology { Size = TopologySize.Tray, Displays = displays, Width = 56, Height = 24, VerticalAlignment = VerticalAlignment.Top },
                    new DisplayTopology { Size = TopologySize.Card, Displays = displays, Width = 480, Height = 200 },
                    new DisplayTopology { Size = TopologySize.Full, Displays = displays, Width = 560, Height = 220, SelectedKey = displays.Count > 0 ? displays[0].Key : null },
                },
            });
        }

        var window = new Window
        {
            Title = "Topology gallery",
            Width = 1240,
            Height = 1400,
        };
        // The background on the rendered element itself, so the PNG is not transparent.
        var page = new Border { Child = rows };
        page.Bind(Border.BackgroundProperty, page.GetResourceObservable("RsContent"));
        window.Content = new ScrollViewer { Content = page };
        if (pngPath is not null)
        {
            window.Opened += (_, _) => Dispatcher.UIThread.Post(() => Save(page, pngPath), DispatcherPriority.Background);
        }

        window.Show();
    }

    private static void Save(Control content, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)content.Bounds.Width, (int)content.Bounds.Height));
        bitmap.Render(content);
        bitmap.Save(path, new PngBitmapEncoderOptions());
        Log.Information("Topology gallery saved to {Path}", path);
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
        yield return ("Missing, optional, off, rotated", [
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
