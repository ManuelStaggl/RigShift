using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RigShift.App.Localization;
using RigShift.App.Services;

namespace RigShift.Avalonia.Services;

/// <summary>One answer of an <see cref="AskDialog"/>: its label, what it returns, and whether Enter picks it.</summary>
internal sealed record AskChoice<T>(string Label, T Value, bool IsDefault = false);

/// <summary>
/// A modal question over the main window: title, text and the answers as buttons, right-aligned in Windows order
/// (the action first, the way out last). Esc and the close button give the way out's value; Enter the default answer.
/// </summary>
internal static class AskDialog
{
    public static Task<T> AskAsync<T>(string title, string text, AskChoice<T> cancel, params AskChoice<T>[] choices) =>
        AskAsync(title, text, [], cancel, choices);

    /// <param name="details">Lines under the text, in a scrolling list: what the answer would bring along.</param>
    public static async Task<T> AskAsync<T>(string title, string text, IReadOnlyList<RestoreLine> details, AskChoice<T> cancel, params AskChoice<T>[] choices)
    {
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(cancel);
        T result = cancel.Value;
        var window = new Window
        {
            Title = title,
            Width = details.Count > 0 ? 540 : 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        window.Bind(Window.BackgroundProperty, window.GetResourceObservable("RsCard"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (AskChoice<T> choice in choices.Append(cancel))
        {
            var button = new Button
            {
                Content = Loc.WithoutAccessKey(choice.Label),
                MinWidth = 96,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsDefault = choice.IsDefault,
                Classes = { choice.IsDefault ? "primary" : "neutral" },
            };
            button.Click += (_, _) =>
            {
                result = choice.Value;
                window.Close();
            };
            buttons.Children.Add(button);
        }

        window.Content = new StackPanel
        {
            Margin = new Thickness(24, 20, 24, 20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = title, Classes = { "h3" }, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = text, Classes = { "secondary" }, TextWrapping = TextWrapping.Wrap },
                Details(details),
                new Border { Height = 8 },
                buttons,
            },
        };
        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };

        global::RigShift.Avalonia.Views.TextScale.Apply(window);
        await ShowModalAsync(window);

        return result;
    }

    /// <summary>The detail lines in a sunken, scrolling well: heading, the path or keys, the warning in amber.</summary>
    private static Border Details(IReadOnlyList<RestoreLine> lines)
    {
        var list = new StackPanel { Spacing = 10, Margin = new Thickness(14, 12) };
        foreach (RestoreLine line in lines)
        {
            var row = new StackPanel { Spacing = 1 };
            row.Children.Add(new TextBlock { Text = line.Heading, Classes = { "strong" }, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(new TextBlock { Text = line.Text, Classes = { "caption" }, FontSize = 12.5, TextWrapping = TextWrapping.Wrap });
            if (line.Warning is { } warning)
            {
                row.Children.Add(new TextBlock { Text = warning, Classes = { "caption", "warning" }, TextWrapping = TextWrapping.Wrap });
            }

            list.Children.Add(row);
        }

        var well = new Border
        {
            CornerRadius = new CornerRadius(8),
            IsVisible = lines.Count > 0,
            Child = new ScrollViewer { MaxHeight = 280, Content = list, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled },
        };
        well.Bind(Border.BackgroundProperty, well.GetResourceObservable("RsDeep"));
        well.Bind(Border.BoxShadowProperty, well.GetResourceObservable("RsWellShadow"));
        return well;
    }

    /// <summary>Modal over the main window; asked from the tray with the window closed, it stands on its own.</summary>
    public static async Task ShowModalAsync(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (App.Owner is { } owner)
        {
            await window.ShowDialog(owner);
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var closed = new TaskCompletionSource();
        window.Closed += (_, _) => closed.TrySetResult();
        window.Show();
        await closed.Task;
    }
}
