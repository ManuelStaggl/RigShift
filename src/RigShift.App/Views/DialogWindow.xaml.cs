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

/// <summary>
/// The question dialog of RigShift 3.0: a heading, one sentence and the choices on the right. Deliberately not the
/// WPF-UI MessageBox – that one paints a destructive button in a filled red, which R-ACT-3 rules out.
/// </summary>
public partial class DialogWindow : FluentWindow
{
    private readonly TaskCompletionSource<int> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _cancelResult;
    private bool _answered;

    private DialogWindow(string title, string message, IReadOnlyList<DialogChoice> choices, int cancelResult)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        _cancelResult = cancelResult;

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
    public static Task<int> AskAsync(string title, string message, IReadOnlyList<DialogChoice> choices, int cancelResult)
    {
        ArgumentNullException.ThrowIfNull(choices);
        var window = new DialogWindow(title, message, choices, cancelResult);
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
