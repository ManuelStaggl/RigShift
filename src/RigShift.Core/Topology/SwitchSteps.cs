using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>The parts of a switch, in the order they run.</summary>
public enum SwitchStepKind
{
    Surround,
    Displays,

    /// <summary>Sound devices and volume, keep-awake and call ducking: undone together if the switch is taken back.</summary>
    Audio,

    /// <summary>"Keep this layout?" and its countdown.</summary>
    Confirmation,

    Apps,

    /// <summary>Remembered windows and desktop icons put back, after the result.</summary>
    Windows,
}

public enum SwitchStepState
{
    Waiting,
    Running,
    Done,
    Failed,
}

/// <summary>A step changed its state; the first report of a kind announces it as <see cref="SwitchStepState.Waiting"/>.</summary>
/// <param name="Reason">Why it failed, in the log's words; <c>null</c> otherwise.</param>
public sealed record SwitchStep(SwitchStepKind Kind, SwitchStepState State, string? Reason = null);

public static class SwitchSteps
{
    /// <summary>The steps a switch to <paramref name="profile"/> runs; what the profile does not use is left out.</summary>
    public static IReadOnlyList<SwitchStepKind> For(Profile profile, bool asks, bool closesApps)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var steps = new List<SwitchStepKind>();
        if (profile.Surround is not null)
        {
            steps.Add(SwitchStepKind.Surround);
        }

        steps.Add(SwitchStepKind.Displays);
        if (profile.Audio is { Playback: not null } or { Recording: not null } or { PlaybackCommunications: not null } or { RecordingCommunications: not null }
            || profile.KeepAwake || profile.DisableCommunicationsDucking)
        {
            steps.Add(SwitchStepKind.Audio);
        }

        if (asks)
        {
            steps.Add(SwitchStepKind.Confirmation);
        }

        if (profile.Apps.Count > 0 || closesApps)
        {
            steps.Add(SwitchStepKind.Apps);
        }

        if (profile.RememberWindows || profile.RemembersDesktopIcons)
        {
            steps.Add(SwitchStepKind.Windows);
        }

        return steps;
    }
}
