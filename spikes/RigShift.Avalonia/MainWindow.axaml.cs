using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Layout;
using Avalonia.Media;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Avalonia.Views;

namespace RigShift.Avalonia;

/// <summary>Title bar, the 60 px icon rail and the page host. Pages are built once and kept, like the WinUI frame's cache.</summary>
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
        AddRail(TopRail, AppPage.FieldOfView, "Nav_Fov", Glyphs.Fov, "FOV");
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
                    Margin = new global::Avalonia.Thickness(32, 72),
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

    /// <param name="shortLabel">The label under the icon when the full name does not fit 60 px; the tooltip keeps the full name.</param>
    private void AddRail(StackPanel panel, AppPage page, string textKey, string data, string? shortLabel = null)
    {
        string text = Loc.WithoutAccessKey(Loc.Instance[textKey]);
        var plate = new Border
        {
            Width = 40,
            Height = 32,
            CornerRadius = new global::Avalonia.CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Center,
            Classes = { "plate" },
            Child = new Path { Data = Geometry.Parse(data), Classes = { "icon" }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var label = new TextBlock
        {
            Text = shortLabel ?? text,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 56,
        };
        var pill = new Border
        {
            Width = 3,
            Height = 20,
            Margin = new global::Avalonia.Thickness(0, 6, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            CornerRadius = new global::Avalonia.CornerRadius(0, 2, 2, 0),
            Classes = { "pill" },
        };
        pill.Bind(Border.BackgroundProperty, pill.GetResourceObservable("RsAccentText"));
        var content = new Grid();
        content.Children.Add(new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { plate, label } });
        content.Children.Add(pill);
        var button = new Button { Content = content, Classes = { "rail" } };
        ToolTip.SetTip(button, text);
        global::Avalonia.Automation.AutomationProperties.SetName(button, text);
        button.Click += (_, _) => Navigate(page);
        _rail[page] = button;
        panel.Children.Add(button);
    }
}
