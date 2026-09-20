using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace RigShift.App.Views;

/// <summary>How a dialog button looks: the rank decides, not the meaning (R-ACT-1, R-ACT-3).</summary>
public enum DialogButtonKind
{
    /// <summary>The default rank; also the way out ("Cancel").</summary>
    Secondary,

    /// <summary>The one accent button, for an action the user set out to do ("Save").</summary>
    Primary,

    /// <summary>Destructive: red fill, never the accent (R-ACT-3, D-03).</summary>
    Danger,
}

/// <summary>One choice a dialog offers. <paramref name="Result"/> is what <see cref="DialogWindow.AskAsync"/> returns.</summary>
public sealed record DialogChoice(string Text, DialogButtonKind Kind, int Result);

/// <summary>One line of the list a dialog can show under its question.</summary>
/// <param name="Heading">What it is and whose, e.g. "Starts · Sim Rig".</param>
/// <param name="Text">The thing itself, e.g. a path with its arguments; may be empty.</param>
/// <param name="Warning">Why this line deserves a second look, or <c>null</c>.</param>
public sealed record DialogDetail(string Heading, string Text, string? Warning = null);

/// <summary>
/// The question dialog of RigShift 3.0: a heading, one sentence and the choices on the right. Deliberately not the
/// WPF-UI MessageBox – that one paints a destructive button in a filled red, which R-ACT-3 rules out.
/// </summary>
public partial class DialogWindow : FluentWindow
{
    private readonly TaskCompletionSource<int> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _cancelResult;
    private bool _answered;

    private DialogWindow(
        string title, string message, IReadOnlyList<DialogChoice> choices, int cancelResult, IReadOnlyList<DialogDetail>? details)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _cancelResult = cancelResult;
        if (details is { Count: > 0 })
        {
            Width = 600; // Paths are long.
            DetailsBox.Visibility = Visibility.Visible;
            foreach (DialogDetail detail in details)
            {
                Details.Children.Add(DetailBlock(detail));
            }
        }

        for (int i = 0; i < choices.Count; i++)
        {
            DialogChoice choice = choices[i];
            var button = new System.Windows.Controls.Button
            {
                Content = choice.Text,
                MinWidth = 100,
                Margin = new Thickness(i == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = i == 0,
                IsCancel = choice.Result == cancelResult,
                Style = (Style)FindResource(choice.Kind switch
                {
                    DialogButtonKind.Primary => "RigShift.Button.Primary",
                    DialogButtonKind.Danger => "RigShift.Button.Danger",
                    _ => "RigShift.Button",
                }),
            };
            button.Click += (_, _) => Answer(choice.Result);
            Buttons.Children.Add(button);
        }

        Loaded += (_, _) => ((System.Windows.Controls.Button)Buttons.Children[0]).Focus();
    }

    /// <summary>Shows the dialog over the active window and returns the chosen <see cref="DialogChoice.Result"/>.</summary>
    /// <param name="cancelResult">What closing the window without choosing means; Esc picks it too.</param>
    /// <param name="details">Lines listed under the question in a box that scrolls; none for a plain question.</param>
    public static Task<int> AskAsync(
        string title, string message, IReadOnlyList<DialogChoice> choices, int cancelResult, IReadOnlyList<DialogDetail>? details = null)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var window = new DialogWindow(title, message, choices, cancelResult, details);
        if (ActiveWindow() is { } owner)
        {
            window.Owner = owner;
        }
        else
        {
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.ShowDialog();
        return window._answer.Task;
    }

    private System.Windows.Controls.TextBlock DetailBlock(DialogDetail detail)
    {
        var block = new System.Windows.Controls.TextBlock
        {
            Margin = new Thickness(0, 4, 0, 4),
            TextWrapping = TextWrapping.Wrap,
            Style = (Style)FindResource("RigShift.Text.Caption"),
        };
        block.Inlines.Add(new System.Windows.Documents.Run(detail.Heading) { Foreground = (System.Windows.Media.Brush)FindResource("RigShift.Brush.TextPrimary") });
        if (detail.Text.Length > 0)
        {
            block.Inlines.Add(new System.Windows.Documents.LineBreak());
            block.Inlines.Add(new System.Windows.Documents.Run(detail.Text) { Foreground = (System.Windows.Media.Brush)FindResource("RigShift.Brush.TextSecondary") });
        }

        if (detail.Warning is { Length: > 0 } warning)
        {
            // Said in words, not by colour alone.
            block.Inlines.Add(new System.Windows.Documents.LineBreak());
            block.Inlines.Add(new System.Windows.Documents.Run("⚠ " + warning) { Foreground = (System.Windows.Media.Brush)FindResource("RigShift.Brush.Warn") });
        }

        return block;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        BrandWindow.ApplyChrome(this, "RigShift.Brush.Page");
    }

    protected override void OnClosed(EventArgs e)
    {
        // Alt+F4 or the window closing otherwise counts as the way out.
        _answer.TrySetResult(_cancelResult);
        base.OnClosed(e);
    }

    private static Window? ActiveWindow()
    {
        Application? app = Application.Current;
        return app?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? (app?.MainWindow is { IsVisible: true } main ? main : null);
    }

    private void Answer(int result)
    {
        if (_answered)
        {
            return;
        }

        _answered = true;
        _answer.TrySetResult(result);
        Close();
    }
}
