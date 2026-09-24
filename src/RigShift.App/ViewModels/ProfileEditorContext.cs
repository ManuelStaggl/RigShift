using RigShift.Core.Abstractions;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

/// <summary>What a profile editor starts from besides the profile: read from the machine and the settings when it opens.</summary>
/// <param name="AppsWaitDevice">The device the apps wait for, with the devices to choose from.</param>
/// <param name="ConfirmationEnabled">Whether switches ask for confirmation at all; "switch without asking" depends on it.</param>
public sealed record ProfileEditorContext(
    IReadOnlyList<AudioDeviceInfo> PlaybackDevices,
    IReadOnlyList<AudioDeviceInfo> RecordingDevices,
    AppsWaitDeviceChoice AppsWaitDevice,
    SurroundState Surround,
    ProfileRulesEditor Rules,
    bool ConfirmationEnabled);
