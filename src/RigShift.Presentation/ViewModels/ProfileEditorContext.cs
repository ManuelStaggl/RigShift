using RigShift.Core.Abstractions;
using RigShift.Core.Topology;

namespace RigShift.App.ViewModels;

/// <summary>What a profile editor starts from besides the profile: read from the machine and the settings when it opens.</summary>
/// <param name="AppsWaitDevice">The device the apps wait for, with the devices to choose from.</param>
/// <param name="SurroundUsedBy">Another profile that switches Surround on; "leave Surround alone" then means off.</param>
public sealed record ProfileEditorContext(
    IReadOnlyList<AudioDeviceInfo> PlaybackDevices,
    IReadOnlyList<AudioDeviceInfo> RecordingDevices,
    AppsWaitDeviceChoice AppsWaitDevice,
    SurroundState Surround,
    ProfileRulesEditor Rules,
    string? SurroundUsedBy = null);
