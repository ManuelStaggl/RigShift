using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RigShift.App.Localization;

namespace RigShift.App.Controls;

/// <summary>
/// The bar at the bottom of a detail while it has unsaved changes: "Unsaved changes · 2 problems · Discard · Save".
/// Save is the primary action of the view while the bar is visible, and disabled while problems remain.
/// </summary>
public sealed class SaveBar : Control
{
    public static readonly DependencyProperty ProblemCountProperty = DependencyProperty.Register(
        nameof(ProblemCount), typeof(int), typeof(SaveBar), new PropertyMetadata(0, (d, _) => ((SaveBar)d).UpdateProblemsText()));

    public static readonly DependencyProperty SaveCommandProperty = DependencyProperty.Register(
        nameof(SaveCommand), typeof(ICommand), typeof(SaveBar), new PropertyMetadata(null));

    public static readonly DependencyProperty DiscardCommandProperty = DependencyProperty.Register(
        nameof(DiscardCommand), typeof(ICommand), typeof(SaveBar), new PropertyMetadata(null));

    private static readonly DependencyPropertyKey ProblemsTextKey = DependencyProperty.RegisterReadOnly(
        nameof(ProblemsText), typeof(string), typeof(SaveBar), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ProblemsTextProperty = ProblemsTextKey.DependencyProperty;

    private static readonly DependencyPropertyKey CanSaveKey = DependencyProperty.RegisterReadOnly(
        nameof(CanSave), typeof(bool), typeof(SaveBar), new PropertyMetadata(true));

    public static readonly DependencyProperty CanSaveProperty = CanSaveKey.DependencyProperty;

    static SaveBar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(SaveBar), new FrameworkPropertyMetadata(typeof(SaveBar)));
    }

    public SaveBar()
    {
        // Texts built in code need the language change by hand (finding I-13); weak, the bar lives shorter than Loc.
        PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                Motion.PlayEnter(this, 16);
            }
        };
        UpdateProblemsText();
    }

    /// <summary>Validation errors in the detail; the save button is disabled while there are any.</summary>
    public int ProblemCount
    {
        get => (int)GetValue(ProblemCountProperty);
        set => SetValue(ProblemCountProperty, value);
    }

    public ICommand? SaveCommand
    {
        get => (ICommand?)GetValue(SaveCommandProperty);
        set => SetValue(SaveCommandProperty, value);
    }

    public ICommand? DiscardCommand
    {
        get => (ICommand?)GetValue(DiscardCommandProperty);
        set => SetValue(DiscardCommandProperty, value);
    }

    /// <summary>"1 problem" / "2 problems", empty without problems.</summary>
    public string ProblemsText => (string)GetValue(ProblemsTextProperty);

    public bool CanSave => (bool)GetValue(CanSaveProperty);

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateProblemsText();

    private void UpdateProblemsText()
    {
        int count = ProblemCount;
        SetValue(CanSaveKey, count == 0);
        SetValue(ProblemsTextKey, count switch
        {
            0 => string.Empty,
            1 => Loc.Format("SaveBar_Problem", count),
            _ => Loc.Format("SaveBar_Problems", count),
        });
    }
}
