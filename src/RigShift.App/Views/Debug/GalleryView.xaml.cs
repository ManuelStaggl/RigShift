using System.Windows;
using System.Windows.Controls;
using RigShift.App.Controls;
using RigShift.Core.Topology;

namespace RigShift.App.Views.Debug;

/// <summary>Debug builds only: every component of Controls.xaml with demo data, rendered by <c>--preview-gallery</c>.</summary>
public partial class GalleryView : UserControl
{
    private static readonly TopologyDisplay[] Desk =
    [
        new() { Key = "left", X = -1920, Y = 540, Width = 1920, Height = 1080, Number = 2, Name = "Links", Mode = "1920×1080 @ 100 Hz" },
        new() { Key = "main", X = 0, Y = 0, Width = 3840, Height = 2160, Number = 1, Name = "XG32UCWG", Mode = "3840×2160 @ 165 Hz", IsPrimary = true, Details = "ASUS XG32UCWG · 3840×2160 @ 165 Hz · HDR an" },
        new() { Key = "right", X = 3840, Y = 540, Width = 1920, Height = 1080, Number = 3, Name = "Rechts", Mode = "1920×1080 @ 100 Hz" },
    ];

    private static readonly TopologyDisplay[] Rig =
    [
        new() { Key = "g9", X = 0, Y = 0, Width = 5120, Height = 1440, Number = 4, Name = "Odyssey G93SC", Mode = "5120×1440 @ 240 Hz", IsPrimary = true },
        new() { Key = "dash", X = 5120, Y = 0, Width = 2880, Height = 1920, Number = 5, Name = "Dash", Mode = "2880×1920 @ 60 Hz", IsOptional = true },
    ];

    private static readonly TopologyDisplay[] States =
    [
        new() { Key = "g9", X = 0, Y = 0, Width = 5120, Height = 1440, Number = 4, Name = "Odyssey G93SC", Mode = "5120×1440 @ 240 Hz", IsPrimary = true },
        new() { Key = "dash", X = 5120, Y = 0, Width = 2880, Height = 1920, Number = 5, Name = "Dash", Mode = "2880×1920 @ 60 Hz", IsOptional = true },
        new() { Key = "off", X = -1920, Y = 180, Width = 1920, Height = 1080, Number = 2, Name = "Links", Mode = "1920×1080 @ 100 Hz", State = TopologyDisplayState.Off },
        new() { Key = "missing", X = 8000, Y = 360, Width = 1920, Height = 1080, Name = "Rechts", State = TopologyDisplayState.Missing },
    ];

    public GalleryView()
    {
        InitializeComponent();
        TopoXs.Displays = Desk;
        TopoS.Displays = Rig;
        TopoM.Displays = Desk;
        TopoL.Displays = States;
        TopoL.SelectedKey = "g9";

        Rows.Items.Add(Row("Schreibtisch", Desk, StatusKind.Ok, "Aktiv · Standard"));
        Rows.Items.Add(Row("Sim Rig", Rig, StatusKind.Ok, "Bereit · Dash fehlt (optional)"));
        Rows.Items.Add(Row("Rig · Dreifach", States, StatusKind.Error, "Blockiert: Surround-Raster fehlt"));
        Rows.SelectedIndex = 1;

        WaitSelect.ItemsSource = new[]
        {
            new Choice("Wheel · Fanatec ClubSport DD (nicht verbunden)"),
            new Choice("Nicht warten"),
        };
        WaitSelect.SelectedIndex = 0;
    }

    /// <summary>Stands in for the view models the pages bind to: the select shows <see cref="Name"/>, not ToString.</summary>
    private sealed record Choice(string Name);

    private static ListBoxItem Row(string name, TopologyDisplay[] displays, StatusKind kind, string status)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["RigShift.Text.BodyStrong"], TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new StatusLine { Kind = kind, Text = status, Margin = new Thickness(0, 2, 0, 0) });
        var row = new DockPanel();
        var thumb = new DisplayTopology { Displays = displays, Size = TopologySize.S, Width = 64, Height = 28, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(thumb, Dock.Left);
        row.Children.Add(thumb);
        row.Children.Add(text);
        return new ListBoxItem { Content = row };
    }
}
