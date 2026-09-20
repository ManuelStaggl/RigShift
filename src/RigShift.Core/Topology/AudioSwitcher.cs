using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The audio part of a switch: default devices and volumes per profile, and what a rollback needs to undo them.
/// Logs under the orchestrator's context, so the log source stays the same.
/// </summary>
internal sealed class AudioSwitcher(IAudioController audio, ILogger log)
{
    private readonly IAudioController _audio = audio;
    private readonly ILogger _log = log;

    public async Task<AudioOutcome> SwitchAsync(AudioAssignment audio, CancellationToken cancellationToken)
    {
        List<AudioStep> steps = AudioSteps(audio);
        if (steps.Count == 0)
        {
            return AudioOutcome.NotConfigured;
        }

        bool complete = true;
        foreach (AudioStep step in steps)
        {
            complete &= await TrySetDefaultAsync(step.Endpoint, step.Roles, cancellationToken);
        }

        foreach ((AudioEndpoint endpoint, int percent) in VolumeSteps(audio))
        {
            complete &= await TrySetVolumeAsync(endpoint, percent, cancellationToken);
        }

        return complete ? AudioOutcome.Applied : AudioOutcome.Incomplete;
    }

    /// <summary>A volume belongs to the chosen device, so it is only set together with one.</summary>
    private static List<(AudioEndpoint Endpoint, int Percent)> VolumeSteps(AudioAssignment audio)
    {
        var steps = new List<(AudioEndpoint, int)>();
        if (audio.Playback is { } playback && audio.PlaybackVolumePercent is { } playbackVolume)
        {
            steps.Add((playback, playbackVolume));
        }

        if (audio.Recording is { } recording && audio.RecordingVolumePercent is { } recordingVolume)
        {
            steps.Add((recording, recordingVolume));
        }

        return steps;
    }

    private async Task<bool> TrySetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken)
    {
        try
        {
            await _audio.SetVolumeAsync(endpoint, percent, cancellationToken);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Setting volume of {Device} to {Volume} % failed", endpoint.FriendlyName, percent);
            return false;
        }
    }

    private static List<AudioStep> AudioSteps(AudioAssignment audio)
    {
        const AudioRoleMask Standard = AudioRoleMask.Console | AudioRoleMask.Multimedia;
        var steps = new List<AudioStep>();

        if (audio.Playback is { } playback)
        {
            steps.Add(new AudioStep(playback, audio.PlaybackCommunications is null ? AudioRoleMask.All : Standard, AudioDirection.Render));
        }

        if (audio.PlaybackCommunications is { } playbackCommunications)
        {
            steps.Add(new AudioStep(playbackCommunications, AudioRoleMask.Communications, AudioDirection.Render));
        }

        if (audio.Recording is { } recording)
        {
            steps.Add(new AudioStep(recording, audio.RecordingCommunications is null ? AudioRoleMask.All : Standard, AudioDirection.Capture));
        }

        if (audio.RecordingCommunications is { } recordingCommunications)
        {
            steps.Add(new AudioStep(recordingCommunications, AudioRoleMask.Communications, AudioDirection.Capture));
        }

        return steps;
    }

    private async Task<bool> TrySetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken)
    {
        try
        {
            bool set = await _audio.SetDefaultAsync(endpoint, roles, cancellationToken);
            if (set)
            {
                _log.Information("Audio default for {Roles} set to {Device}", roles, endpoint.FriendlyName);
            }
            else
            {
                _log.Warning("Audio device {Device} is not active, default for {Roles} unchanged", endpoint.FriendlyName, roles);
            }

            return set;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Setting audio default for {Roles} to {Device} failed", roles, endpoint.FriendlyName);
            return false;
        }
    }

    /// <summary>
    /// Remembers the current default device per direction the profile changes, and the volume of every device whose
    /// volume it sets, so a rollback can restore them. Windows keeps one default per role – sound on the speakers,
    /// calls on the headset – so each role the switch touches goes back to the device that held it.
    /// </summary>
    public async Task<AudioRestore> CaptureAsync(AudioAssignment audio, CancellationToken cancellationToken)
    {
        var defaults = new List<DefaultRestore>();
        foreach (IGrouping<AudioDirection, AudioStep> direction in AudioSteps(audio).GroupBy(s => s.Direction))
        {
            try
            {
                IReadOnlyList<AudioDeviceInfo> devices = await _audio.ListAsync(direction.Key, cancellationToken);
                AudioRoleMask touched = direction.Aggregate(AudioRoleMask.None, (mask, step) => mask | step.Roles);
                foreach (AudioDeviceInfo device in devices)
                {
                    if ((device.DefaultRoles & touched) is var held and not AudioRoleMask.None)
                    {
                        defaults.Add(new DefaultRestore(device.Endpoint, held));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Could not read current {Direction} default; rollback will leave audio unchanged", direction.Key);
            }
        }

        var volumes = new List<(AudioEndpoint, int)>();
        foreach ((AudioEndpoint endpoint, _) in VolumeSteps(audio))
        {
            try
            {
                volumes.Add((endpoint, await _audio.GetVolumeAsync(endpoint, cancellationToken)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Could not read volume of {Device}; rollback will leave it unchanged", endpoint.FriendlyName);
            }
        }

        return new AudioRestore(defaults, volumes);
    }

    public async Task RestoreAsync(AudioRestore restore, CancellationToken cancellationToken)
    {
        foreach (DefaultRestore item in restore.Defaults)
        {
            await TrySetDefaultAsync(item.Endpoint, item.Roles, cancellationToken);
        }

        foreach ((AudioEndpoint endpoint, int percent) in restore.Volumes)
        {
            await TrySetVolumeAsync(endpoint, percent, cancellationToken);
        }
    }

    internal sealed record DefaultRestore(AudioEndpoint Endpoint, AudioRoleMask Roles);

    internal sealed record AudioRestore(IReadOnlyList<DefaultRestore> Defaults, IReadOnlyList<(AudioEndpoint Endpoint, int Percent)> Volumes)
    {
        public static AudioRestore Nothing { get; } = new([], []);
    }

    private sealed record AudioStep(AudioEndpoint Endpoint, AudioRoleMask Roles, AudioDirection Direction);
}
