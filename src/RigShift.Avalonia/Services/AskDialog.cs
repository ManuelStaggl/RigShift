using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using RigShift.App.Localization;

namespace RigShift.Avalonia.Services;

/// <summary>One answer of an <see cref="AskDialog"/>: its label, what it returns, and whether Enter picks it.</summary>
internal sealed record AskChoice<T>(string Label, T Value, bool IsDefault = false);

/// <summary>
/// A modal question over the main window: title, text and the answers as buttons, right-aligned in Windows order
/// (the action first, the way out last). Esc and the close button give the way out's value; Enter the default answer.
/// </summary>
internal static class AskDialog
{
    public static async Task<T> AskAsync<T>(string title, string text, AskChoice<T> cancel, params AskChoice<T>[] choices)
    {
        ArgumentNullException.ThrowIfNull(cancel);
        T result = cancel.Value;
        var window = new Window
        {
            Title = title,
            Width = 440,
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

        await ShowModalAsync(window);

        return result;
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
