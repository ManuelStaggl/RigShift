using Avalonia;
using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
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
        ApplyTextScale();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
            {
                OpenPalette();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// Windows' text size (100–225 %) as a scale on the pages, capped so the smallest window still fits the work area;
    /// the title bar and rail keep their size, like the WPF app's TextScale.
    /// </summary>
    private void ApplyTextScale()
    {
        double factor = Math.Clamp((TextScaleSetting.Read() ?? 100) / 100d, 1, 2.25);
        double roomWidth = double.PositiveInfinity, roomHeight = double.PositiveInfinity;
        if (Screens.Primary is { } screen)
        {
            roomWidth = screen.WorkingArea.Width / screen.Scaling;
            roomHeight = screen.WorkingArea.Height / screen.Scaling;
            factor = Math.Min(factor, Math.Min(roomWidth / MinWidth, roomHeight / MinHeight));
        }

        if (factor > 1)
        {
            Scaled.LayoutTransform = new ScaleTransform(factor, factor);
            Width = Math.Min(Width * factor, roomWidth);
            Height = Math.Min(Height * factor, roomHeight);
            Serilog.Log.Information("Text size {Factor:0.##}x applied to the pages", factor);
        }
    }

    private sealed record PaletteEntry(string Title, string? Hint, string Glyph, Action Run);

    private List<PaletteEntry> _entries = [];

    /// <summary>Profiles first (switch, then edit), then the pages; built on every open so it follows the catalog.</summary>
    public void OpenPalette()
    {
        var profiles = App.Provider.GetRequiredService<ProfileCatalog>().Items;
        var overview = App.Provider.GetRequiredService<OverviewViewModel>();
        _entries =
        [
            .. profiles.Where(p => !p.IsActive).Select(p => new PaletteEntry(
                $"Switch to {p.Name}", p.HasHotkey ? p.HotkeyText : null, Glyphs.Switch, () => overview.SwitchCommand.Execute(p))),
            .. profiles.Select(p => new PaletteEntry($"Edit {p.Name}", "Profile", Glyphs.Profiles, () =>
            {
                Navigate(AppPage.Profiles);
                App.Provider.GetRequiredService<ProfilesViewModel>().SelectedItem = p;
            })),
            .. _rail.Keys.Select(page => new PaletteEntry(
                "Go to " + global::Avalonia.Automation.AutomationProperties.GetName(_rail[page]), "Page", RailGlyph(page), () => Navigate(page))),
        ];
        PaletteQuery.Text = string.Empty;
        Filter(string.Empty);
        Palette.IsVisible = true;
        PaletteQuery.Focus();
    }

    private static string RailGlyph(AppPage page) => page switch
    {
        AppPage.Overview => Glyphs.Overview,
        AppPage.Profiles => Glyphs.Profiles,
        AppPage.Games => Glyphs.Games,
        AppPage.FieldOfView => Glyphs.Fov,
        AppPage.Settings => Glyphs.Settings,
        _ => Glyphs.Help,
    };

    private void ClosePalette() => Palette.IsVisible = false;

    private void Filter(string query)
    {
        PaletteList.ItemsSource = _entries
            .Where(e => e.Title.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase))
            .Select(Row)
            .ToList();
        PaletteList.SelectedIndex = PaletteList.ItemCount > 0 ? 0 : -1;
        PaletteEmpty.IsVisible = PaletteList.ItemCount == 0;
    }

    private static Grid Row(PaletteEntry entry)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Tag = entry, MinHeight = 24 };
        row.Children.Add(new Path { Data = Geometry.Parse(entry.Glyph), Classes = { "icon", "small" }, VerticalAlignment = VerticalAlignment.Center });
        var title = new TextBlock { Text = entry.Title, Classes = { "label" } };
        Grid.SetColumn(title, 1);
        row.Children.Add(title);
        if (entry.Hint is { } hint)
        {
            var tag = new TextBlock { Text = hint, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(tag, 2);
            row.Children.Add(tag);
        }

        return row;
    }

    private void Run(object? item)
    {
        if (item is Grid { Tag: PaletteEntry entry })
        {
            ClosePalette();
            entry.Run();
        }
    }

    private void OnSearchClick(object? sender, RoutedEventArgs e) => OpenPalette();

    private void OnPaletteQueryChanged(object? sender, TextChangedEventArgs e) => Filter(PaletteQuery.Text ?? string.Empty);

    private void OnPaletteDimPressed(object? sender, PointerPressedEventArgs e) => ClosePalette();

    private void OnPaletteTapped(object? sender, TappedEventArgs e) => Run(PaletteList.SelectedItem);

    private void OnPaletteKeyDown(object? sender, KeyEventArgs e)
    {
        int count = PaletteList.ItemCount;
        switch (e.Key)
        {
            case Key.Escape:
                ClosePalette();
                break;
            case Key.Enter:
                Run(PaletteList.SelectedItem);
                break;
            case Key.Down when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex + 1) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedIndex);
                break;
            case Key.Up when count > 0:
                PaletteList.SelectedIndex = (PaletteList.SelectedIndex - 1 + count) % count;
                PaletteList.ScrollIntoView(PaletteList.SelectedIndex);
                break;
            default:
                return;
        }

        e.Handled = true;
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
