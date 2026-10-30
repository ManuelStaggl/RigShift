using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Views;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>
/// Tray test for #155: Avalonia's own TrayIcon (Shell_NotifyIcon underneath) with the native context menu, and a popup
/// window above the taskbar on left click, listing the profiles like the WinUI tray popup.
/// </summary>
internal sealed class SpikeTray
{
    private readonly App _app;
    private readonly OverviewViewModel _overview;
    private Window? _popup;

    public SpikeTray(App app)
    {
        _app = app;
        _overview = App.Provider.GetRequiredService<OverviewViewModel>();

        var menu = new NativeMenu();
        foreach (ProfileItem item in _overview.Catalog.Items)
        {
            var entry = new NativeMenuItem(item.Name) { ToggleType = MenuItemToggleType.Radio, IsChecked = item.IsActive };
            entry.Click += (_, _) => _overview.SwitchCommand.Execute(item);
            menu.Items.Add(entry);
        }

        menu.Items.Add(new NativeMenuItemSeparator());
        var open = new NativeMenuItem(Loc.WithoutAccessKey(Loc.Instance["Nav_Overview"]));
        open.Click += (_, _) => _app.ShowMainWindow(AppPage.Overview);
        menu.Items.Add(open);
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _app.QuitByUser();
        menu.Items.Add(quit);

        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://RigShift.Avalonia/Assets/tray.ico"))),
            ToolTipText = "RigShift",
            Menu = menu,
            IsVisible = true,
        };
        icon.Clicked += (_, _) => TogglePopup();
        TrayIcon.SetIcons(app, [icon]);
        Log.Information("Spike tray icon added with {Count} menu entries", menu.Items.Count);
    }

    public void TogglePopup()
    {
        if (_popup is { IsVisible: true })
        {
            _popup.Hide();
            return;
        }

        _popup ??= Build();
        _popup.Show();
        _popup.Activate();
    }

    private Window Build()
    {
        var rows = new StackPanel { Spacing = 2 };
        foreach (ProfileItem item in _overview.Catalog.Items)
        {
            var row = new Button
            {
                Classes = { "trayrow" },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Content = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("56,*,Auto"),
                    ColumnSpacing = 10,
                    Children =
                    {
                        new DisplayTopology { Displays = item.Topology, Size = TopologySize.Tray, Height = 24 },
                        Cell(new TextBlock { Text = item.Name, Classes = { "strong" }, FontSize = 13, VerticalAlignment = VerticalAlignment.Center }, 1),
                        Cell(new TextBlock { Text = item.IsActive ? Loc.Instance["Profile_Active"] : item.HotkeyText, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center }, 2),
                    },
                },
            };
            row.Click += (_, _) => _overview.SwitchCommand.Execute(item);
            rows.Children.Add(row);
        }

        var openButton = new Button { Content = Loc.WithoutAccessKey(Loc.Instance["Nav_Overview"]), Classes = { "neutral" } };
        openButton.Click += (_, _) => { _popup?.Hide(); _app.ShowMainWindow(AppPage.Overview); };

        var popup = new Window
        {
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            Background = Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
            Opacity = 0,
            Content = Themed(new Border
            {
                Margin = new Thickness(0),
                Padding = new Thickness(8),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "RigShift", Classes = { "caption" }, Margin = new Thickness(6, 2) },
                        rows,
                        new Border { Classes = { "divider" } },
                        openButton,
                    },
                },
            }),
        };
        // Placed once layout has measured the height; invisible until then, so it never flashes elsewhere.
        popup.Opened += (_, _) => Dispatcher.UIThread.Post(() => { Place(popup); popup.Opacity = 1; }, DispatcherPriority.Loaded);
        popup.Deactivated += (_, _) => popup.Hide();
        return popup;
    }

    // shortcut: always the primary screen's bottom-right corner; taskbar on top/left and the cursor's monitor are still open (#155 gaming-PC checks).
    private static void Place(Window popup)
    {
        if (popup.Screens.Primary is not { } screen)
        {
            return;
        }

        double scale = screen.Scaling;
        PixelRect area = screen.WorkingArea;
        int gap = (int)(12 * scale);
        var size = new PixelSize((int)(popup.Bounds.Width * scale), (int)(popup.Bounds.Height * scale));
        popup.Position = new PixelPoint(area.Right - size.Width - gap, area.Bottom - size.Height - gap);
        Log.Information("Spike tray popup at {X},{Y} size {W}x{H} in working area {Area}", popup.Position.X, popup.Position.Y, size.Width, size.Height, area);
    }

    private static Border Themed(Border border)
    {
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable("RsPop"));
        border.Bind(Border.BorderBrushProperty, border.GetResourceObservable("RsOutline"));
        return border;
    }

    private static Control Cell(Control control, int column)
    {
        Grid.SetColumn(control, column);
        return control;
    }
}
