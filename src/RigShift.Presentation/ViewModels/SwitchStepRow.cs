using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

/// <summary>One step of the running switch in the tray popup and the overview: what it is and how far it got.</summary>
public sealed partial class SwitchStepRow(SwitchStepKind kind) : ObservableObject
{
    public SwitchStepKind Kind { get; } = kind;

    public string Label => Loc.Instance["Step_" + Kind];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWaiting), nameof(IsRunning), nameof(IsDone), nameof(IsFailed), nameof(Text))]
    public partial SwitchStepState State { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(HasReason))]
    public partial string? Reason { get; set; }

    public bool IsWaiting => State == SwitchStepState.Waiting;

    public bool IsRunning => State == SwitchStepState.Running;

    public bool IsDone => State == SwitchStepState.Done;

    public bool IsFailed => State == SwitchStepState.Failed;

    public bool HasReason => IsFailed && !string.IsNullOrWhiteSpace(Reason);

    /// <summary>"Displays: done" – what a screen reader announces when the step changes.</summary>
    public string Text => Loc.Format("Step_State", Label, Loc.Instance["StepState_" + State]);
}
