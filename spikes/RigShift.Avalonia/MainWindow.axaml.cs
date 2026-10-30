using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Avalonia.Views;

namespace RigShift.Avalonia;

/// <summary>Title bar, the 64 px icon rail and the page host. Pages are built once and kept, like the WinUI frame's cache.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<AppPage, Control> _pages = [];
    private readonly Dictionary<AppPage, Button> _rail = [];

    public MainWindow()
    {
        InitializeComponent();
        AddRail(TopRail, AppPage.Overview, "Nav_Overview", Glyphs.Overview);
        AddRail(TopRail, AppPage.Profiles, "Nav_Profiles", Glyphs.Profiles);
        AddRail(TopRail, AppPage.Games, "Nav_Games", Glyphs.Games);
        AddRail(TopRail, AppPage.FieldOfView, "Nav_Fov", Glyphs.Fov);
        AddRail(BottomRail, AppPage.Settings, "Nav_Settings", Glyphs.Settings);
        AddRail(BottomRail, AppPage.Help, "Nav_About", Glyphs.Help);
    }

    public void Navigate(AppPage page)
    {
        if (!_pages.TryGetValue(page, out Control? view))
        {
            view = page switch
            {
                AppPage.Overview => new OverviewView(),
                AppPage.Profiles => new ProfileSheetView(),
                _ => new TextBlock
                {
                    Text = "Not part of the spike.",
                    Margin = new global::Avalonia.Thickness(32),
                    Classes = { "secondary" },
                },
            };
            _pages[page] = view;
        }

        Host.Content = view;
        foreach ((AppPage key, Button button) in _rail)
        {
            button.Classes.Set("selected", key == page);
        }
    }

    /// <summary>A rail entry: icon only, the name in the tooltip; selected = accent tile with a duotone icon and the marker on the left edge.</summary>
    private void AddRail(StackPanel panel, AppPage page, string textKey, string data)
    {
        string text = Loc.WithoutAccessKey(Loc.Instance[textKey]);
        var tile = new Border
        {
            Width = 44,
            Height = 40,
            CornerRadius = new global::Avalonia.CornerRadius(10),
            Classes = { "plate" },
            Child = new Path { Data = Geometry.Parse(data), Classes = { "icon", "rail" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var marker = new Border
        {
            Width = 3,
            Height = 18,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new global::Avalonia.CornerRadius(0, 3, 3, 0),
            Classes = { "pill" },
        };
        var content = new Grid();
        content.Children.Add(tile);
        content.Children.Add(marker);
        var button = new Button { Content = content, Classes = { "rail" } };
        ToolTip.SetTip(button, text);
        ToolTip.SetPlacement(button, PlacementMode.Right);
        global::Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => Navigate(page);
        _rail[page] = button;
        panel.Children.Add(button);
    }
}
