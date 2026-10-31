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
using Avalonia.Threading;
using RigShift.Avalonia.Views;
using RigShift.Windows.Ui;

namespace RigShift.Avalonia;

/// <summary>Title bar, the 64 px icon rail and the page host. Pages are built once and kept, like the WinUI frame's cache.</summary>
public sealed partial class MainWindow : Window
{
    private readonly Dictionary<AppPage, Control> _pages = [];
    private readonly Dictionary<AppPage, Button> _rail = [];
    private readonly Dictionary<AppPage, string> _railText = [];

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
        ApplyTexts();
        Loc.Instance.PropertyChanged += (_, _) => ApplyTexts();
        Opened += (_, _) => Dispatcher.UIThread.Post(KeepOnScreen, DispatcherPriority.Loaded);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.K && e.KeyModifiers == KeyModifiers.Control)
            {
                OpenPalette();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && Palette.IsVisible)
            {
                // Wherever the focus went inside the open palette.
                ClosePalette();
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
        (double roomWidth, double roomHeight) = TextScale.Room(this);
        double factor = TextScaleSetting.Factor(TextScaleSetting.Read(), roomWidth, roomHeight, MinWidth, MinHeight);
        if (factor > 1)
        {
            Scaled.LayoutTransform = new ScaleTransform(factor, factor);
            Width = Math.Min(Width * factor, roomWidth);
            Height = Math.Min(Height * factor, roomHeight);
            Serilog.Log.Information("Text size {Factor:0.##}x applied to the pages", factor);
        }
    }

    private void ApplyTexts()
    {
        SearchText.Text = Loc.Instance["Palette_Search"];
        SearchKey.Text = Loc.Instance["Palette_Shortcut"];
        global::Avalonia.Automation.AutomationProperties.SetName(Search, Loc.Instance["Palette_SearchName"]);
        PaletteQuery.PlaceholderText = Loc.Instance["Palette_Placeholder"];
        global::Avalonia.Automation.AutomationProperties.SetName(PaletteQuery, Loc.Instance["Palette_Search"]);
        PaletteEmpty.Text = Loc.Instance["Palette_Empty"];
        foreach ((AppPage page, Button button) in _rail)
        {
            string text = Loc.WithoutAccessKey(Loc.Instance[_railText[page]]);
            ToolTip.SetTip(button, text);
            global::Avalonia.Automation.AutomationProperties.SetName(button, text);
        }
    }

    /// <summary>
    /// Once shown, a window that sticks out of its monitor's work area is pulled back in, onto the primary monitor when
    /// it does not fit at its size, like the WPF app's windows.
    /// </summary>
    private void KeepOnScreen()
    {
        if (WindowState != WindowState.Normal || TryGetPlatformHandle()?.Handle is not { } handle)
        {
            return;
        }

        if (NativeWindow.FitIntoMonitor(handle, Width, Height) is { } fit)
        {
            Serilog.Log.Information(
                fit.ToPrimary
                    ? "Main window did not fit its monitor, moved to the primary monitor at {Bounds}"
                    : "Main window opened outside its monitor's work area, moved to {Bounds}",
                fit.Bounds);
        }
    }

    private sealed record PaletteEntry(string Title, string? Hint, string Glyph, Action Run);

    private List<PaletteEntry> _entries = [];

    /// <summary>Switch to a profile, play a game, edit a profile, go to a page; built on every open so it follows the catalogs.</summary>
    public void OpenPalette()
    {
        var profiles = App.Provider.GetRequiredService<ProfileCatalog>().Items;
        var overview = App.Provider.GetRequiredService<OverviewViewModel>();
        _entries =
        [
            .. profiles.Where(p => !p.IsActive).Select(p => new PaletteEntry(
                Loc.Format("Palette_SwitchTo", p.Name), p.HasHotkey ? p.HotkeyText : null, Glyphs.Switch, () => overview.SwitchCommand.Execute(p))),
            .. overview.Games.Items.Where(g => g.CanPlay).Select(g => new PaletteEntry(
                g.PlayText, g.ShowsHotkey ? g.HotkeyText : Loc.Instance["Palette_KindGame"], Glyphs.Play, () => overview.PlayCommand.Execute(g))),
            .. profiles.Select(p => new PaletteEntry(Loc.Format("Palette_Edit", p.Name), Loc.Instance["Palette_KindProfile"], Glyphs.Profiles, () =>
            {
                Navigate(AppPage.Profiles);
                App.Provider.GetRequiredService<ProfilesViewModel>().SelectedItem = p;
            })),
            .. _rail.Keys.Select(page => new PaletteEntry(
                Loc.Format("Palette_GoTo", global::Avalonia.Automation.AutomationProperties.GetName(_rail[page])),
                Loc.Instance["Palette_KindPage"], RailGlyph(page), () => Navigate(page))),
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
        global::Avalonia.Automation.AutomationProperties.SetName(row, entry.Hint is { } kind ? $"{entry.Title}, {kind}" : entry.Title);
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
                AppPage.Games => new GamesView(),
                AppPage.FieldOfView => new FovView(),
                AppPage.Settings => new SettingsView(),
                _ => new HelpView(),
            };
            _pages[page] = view;
        }

        Host.Content = view;
        foreach ((AppPage key, Button button) in _rail)
        {
            button.Classes.Set("selected", key == page);
        }
    }

    /// <summary>A rail entry: icon only, the name in the tooltip (set by <see cref="ApplyTexts"/>); selected = accent tile with a duotone icon and the marker on the left edge.</summary>
    private void AddRail(StackPanel panel, AppPage page, string textKey, string data)
    {
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
        ToolTip.SetPlacement(button, PlacementMode.Right);
        button.Click += (_, _) => Navigate(page);
        _rail[page] = button;
        _railText[page] = textKey;
        panel.Children.Add(button);
    }
}
