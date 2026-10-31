using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RigShift.App.Localization;
using RigShift.Core.Profiles;

namespace RigShift.Avalonia.Views;

/// <summary>A large number and name, centred on one display for a few seconds.</summary>
internal sealed class IdentifyWindow : Window
{
    private static readonly TimeSpan ShowFor = TimeSpan.FromSeconds(3);

    private IdentifyWindow(int number, string name)
    {
        Title = number.ToString(Loc.Instance.Culture) + " · " + name;
        WindowDecorations = global::Avalonia.Controls.WindowDecorations.None;
        Topmost = true;
        ShowActivated = false;
        ShowInTaskbar = false;
        CanResize = false;
        Width = 320;
        Height = 220;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Content = new Border
        {
            Background = Brush("RsCard"),
            BorderBrush = Brush("RsOutline"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = number.ToString(Loc.Instance.Culture), FontSize = 96, LineHeight = 112, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Foreground = Brush("RsAccentText") },
                    new TextBlock { Text = name, HorizontalAlignment = HorizontalAlignment.Center, Classes = { "subtitle", "secondary" } },
                },
            },
        };
    }

    private static IBrush? Brush(string key) =>
        Application.Current!.TryGetResource(key, Application.Current.ActualThemeVariant, out object? value) ? value as IBrush : null;

    public static void ShowAll(IEnumerable<(int Number, string Name, DisplayAssignment Mode)> displays)
    {
        foreach ((int number, string name, DisplayAssignment mode) in displays)
        {
            var window = new IdentifyWindow(number, name);
            // Position is in physical pixels, the size in DIPs of the display it lands on.
            window.Position = new PixelPoint(mode.PositionX + (mode.Width / 2), mode.PositionY + (mode.Height / 2));
            window.Opened += (_, _) =>
            {
                double scale = window.DesktopScaling;
                window.Position = new PixelPoint(
                    mode.PositionX + (int)((mode.Width - (window.Width * scale)) / 2),
                    mode.PositionY + (int)((mode.Height - (window.Height * scale)) / 2));
            };
            window.Show();
            DispatcherTimer.RunOnce(window.Close, ShowFor);
        }
    }
}
