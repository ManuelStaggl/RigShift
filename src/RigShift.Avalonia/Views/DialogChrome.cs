using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace RigShift.Avalonia.Views;

/// <summary>
/// The dialogs' own title bar, like the main window's: Avalonia's drawn caption buttons over a 40 px strip that drags
/// the window, no maximise. A dialog names itself in its heading, so the strip carries text only when given a caption.
/// Call after <see cref="TextScale.Apply"/>: the strip stays 40 px whatever the text size, matching the hit-test height.
/// </summary>
internal static class DialogChrome
{
    private const double BarHeight = 40;

    public static void Apply(Window window, string? caption = null, bool canMinimize = false)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.ExtendClientAreaToDecorationsHint = true;
        window.ExtendClientAreaTitleBarHeightHint = BarHeight;
        window.CanMaximize = false;
        window.CanMinimize = canMinimize;
        if (window.Content is not Control content)
        {
            return;
        }

        // Taken out of the window first: a control has one parent.
        window.Content = null;
        var bar = new Border { Height = BarHeight, Background = Brushes.Transparent };
        if (caption is not null)
        {
            bar.Child = new TextBlock
            {
                Text = caption,
                Margin = new global::Avalonia.Thickness(16, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Classes = { "caption" },
                IsHitTestVisible = false,
            };
        }

        WindowDecorationProperties.SetElementRole(bar, WindowDecorationsElementRole.TitleBar);
        DockPanel.SetDock(bar, Dock.Top);
        window.Content = new DockPanel { Children = { bar, content } };
    }
}
