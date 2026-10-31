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
/// (the action first, Cancel last). Esc and the close button give the cancel value; Enter the default answer.
/// </summary>
internal static class AskDialog
{
    public static async Task<T> AskAsync<T>(string title, string text, T cancel, params AskChoice<T>[] choices)
    {
        T result = cancel;
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
        foreach (AskChoice<T> choice in choices.Append(new AskChoice<T>(Loc.Instance["Common_Cancel"], cancel)))
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

        if (App.Owner is { } owner)
        {
            await window.ShowDialog(owner);
        }
        else
        {
            // Asked from the tray with the window closed: no owner to block, so it stands on its own.
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }

        return result;
    }
}
