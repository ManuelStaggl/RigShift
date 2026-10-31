using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace RigShift.Avalonia.Views;

/// <summary>The step list of a switch, shared by the tray popup and the overview.</summary>
public sealed partial class SwitchStepsView : UserControl
{
    public static readonly StyledProperty<IEnumerable?> StepsProperty =
        AvaloniaProperty.Register<SwitchStepsView, IEnumerable?>(nameof(Steps));

    public SwitchStepsView()
    {
        InitializeComponent();
    }

    public IEnumerable? Steps
    {
        get => GetValue(StepsProperty);
        set => SetValue(StepsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StepsProperty)
        {
            List.ItemsSource = Steps;
        }
    }
}
