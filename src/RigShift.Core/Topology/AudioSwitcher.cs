using System.Text.RegularExpressions;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The audio part of a switch: default devices and volumes per profile, and what a rollback needs to undo them.
/// Logs under the orchestrator's context, so the log source stays the same.
/// </summary>
internal sealed partial class AudioSwitcher(IAudioController audio, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IAudioController _audio = audio;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;

    /// <param name="displaysTurnedOn">
    /// The switch turned displays on: a device that is there but not active yet may be one of theirs, and is waited
    /// for up to <see cref="SwitchOptions.AudioWakeBudget"/> instead of given up at once.
    /// </param>
    public async Task<AudioOutcome> SwitchAsync(AudioAssignment audio, bool displaysTurnedOn, CancellationToken cancellationToken)
    {
        List<AudioStep> steps = AudioSteps(audio);
        if (steps.Count == 0)
        {
            return AudioOutcome.NotConfigured;
        }

        audio = await ResolveAsync(audio, cancellationToken);
        steps = AudioSteps(audio);
        DateTimeOffset? wakeDeadline = displaysTurnedOn ? _time.GetUtcNow() + _options.AudioWakeBudget : null;
        bool complete = true;
        foreach (AudioStep step in steps)
        {
            complete &= await SetDefaultAsync(step.Endpoint, step.Roles, step.Direction, wakeDeadline, cancellationToken);
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

    /// <summary>
    /// Sets the default. A device that is there but not active is tried again until <paramref name="wakeDeadline"/>, if
    /// there is one: the sound device of a display that was just switched on becomes active a moment after its picture,
    /// and giving up at once left the sound on the wrong device (finding K-02).
    /// </summary>
    private async Task<bool> SetDefaultAsync(
        AudioEndpoint endpoint, AudioRoleMask roles, AudioDirection direction, DateTimeOffset? wakeDeadline, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        bool retried = false;
        while (true)
        {
            bool? set = await TrySetDefaultAsync(endpoint, roles, cancellationToken);
            if (set == true)
            {
                if (retried)
                {
                    _log.Information("Audio device {Device} became active after {Milliseconds:0} ms", endpoint.FriendlyName,
                        _time.GetElapsedTime(started).TotalMilliseconds);
                }

                _log.Information("Audio default for {Roles} set to {Device}", roles, endpoint.FriendlyName);
                return true;
            }

            if (set is null || wakeDeadline is not { } deadline || _time.GetUtcNow() >= deadline
                || !await IsPresentAsync(endpoint, direction, cancellationToken))
            {
                if (set == false)
                {
                    _log.Warning("Audio device {Device} is not active, default for {Roles} unchanged", endpoint.FriendlyName, roles);
                }

                return false;
            }

            retried = true;
            await Task.Delay(_options.AudioWakePollInterval, _time, cancellationToken);
        }
    }

    /// <returns>True when set, false when the device is not active, <c>null</c> when the call failed.</returns>
    private async Task<bool?> TrySetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken)
    {
        try
        {
            return await _audio.SetDefaultAsync(endpoint, roles, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Setting audio default for {Roles} to {Device} failed", roles, endpoint.FriendlyName);
            return null;
        }
    }

    /// <summary>Whether Windows knows the device at all, active or not: one that is not there will not wake up.</summary>
    private async Task<bool> IsPresentAsync(AudioEndpoint endpoint, AudioDirection direction, CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = await _audio.ListAsync(direction, cancellationToken);
            return devices.Any(d => string.Equals(d.Endpoint.EndpointId, endpoint.EndpointId, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Audio devices could not be listed while waiting for {Device}", endpoint.FriendlyName);
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
        audio = await ResolveAsync(audio, cancellationToken);
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
                        defaults.Add(new DefaultRestore(device.Endpoint, held, direction.Key));
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

    /// <summary>
    /// Puts the captured defaults back. The old arrangement's displays have just come back on, and with them maybe the
    /// sound device the defaults were on, so an inactive one is waited for like after a switch.
    /// </summary>
    public async Task RestoreAsync(AudioRestore restore, CancellationToken cancellationToken)
    {
        DateTimeOffset wakeDeadline = _time.GetUtcNow() + _options.AudioWakeBudget;
        foreach (DefaultRestore item in restore.Defaults)
        {
            await SetDefaultAsync(item.Endpoint, item.Roles, item.Direction, wakeDeadline, cancellationToken);
        }

        foreach ((AudioEndpoint endpoint, int percent) in restore.Volumes)
        {
            await TrySetVolumeAsync(endpoint, percent, cancellationToken);
        }
    }

    /// <summary>
    /// The assignment with every device Windows does not know under its saved ID replaced by the one active device of the
    /// same direction and name (v4 finding K-08): a USB headset or DAC on another port gets a new ID. Between two devices
    /// of that name nothing is guessed, and a device that is there but not active stays, so it can be waited for.
    /// </summary>
    private async Task<AudioAssignment> ResolveAsync(AudioAssignment audio, CancellationToken cancellationToken)
    {
        var lists = new Dictionary<AudioDirection, IReadOnlyList<AudioDeviceInfo>?>();

        async Task<AudioEndpoint?> ResolveOne(AudioEndpoint? saved, AudioDirection direction)
        {
            if (saved is null)
            {
                return null;
            }

            if (!lists.TryGetValue(direction, out IReadOnlyList<AudioDeviceInfo>? devices))
            {
                devices = await TryListAsync(direction, cancellationToken);
                lists[direction] = devices;
            }

            if (devices is null || devices.Any(d => string.Equals(d.Endpoint.EndpointId, saved.EndpointId, StringComparison.OrdinalIgnoreCase)))
            {
                return saved;
            }

            List<AudioDeviceInfo> named = [.. devices.Where(d => d.IsActive && SameDevice(d.Endpoint.FriendlyName, saved.FriendlyName))];
            if (named.Count != 1)
            {
                return saved;
            }

            _log.Warning("Audio device {Device} is not known under its saved ID any more; using the active device of that name, {Found}",
                saved.FriendlyName, named[0].Endpoint.FriendlyName);
            return named[0].Endpoint;
        }

        return audio with
        {
            Playback = await ResolveOne(audio.Playback, AudioDirection.Render),
            PlaybackCommunications = await ResolveOne(audio.PlaybackCommunications, AudioDirection.Render),
            Recording = await ResolveOne(audio.Recording, AudioDirection.Capture),
            RecordingCommunications = await ResolveOne(audio.RecordingCommunications, AudioDirection.Capture),
        };
    }

    /// <returns>The devices, or <c>null</c> when they could not be listed – then the saved ones stay.</returns>
    private async Task<IReadOnlyList<AudioDeviceInfo>?> TryListAsync(AudioDirection direction, CancellationToken cancellationToken)
    {
        try
        {
            return await _audio.ListAsync(direction, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "{Direction} audio devices could not be listed; the profile's devices are used as saved", direction);
            return null;
        }
    }

    /// <summary>
    /// Names compare without case and without the number Windows gives a second instance of a device: "Speakers
    /// (2- USB Audio DAC)" after the DAC was plugged into another port is still "Speakers (USB Audio DAC)".
    /// </summary>
    private static bool SameDevice(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) && string.Equals(Unnumbered(a), Unnumbered(b), StringComparison.OrdinalIgnoreCase);

    private static string Unnumbered(string name) => NumberedInstance().Replace(name.Trim(), "(");

    [GeneratedRegex(@"\(\d+-\s*", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NumberedInstance();

    internal sealed record DefaultRestore(AudioEndpoint Endpoint, AudioRoleMask Roles, AudioDirection Direction);

    internal sealed record AudioRestore(IReadOnlyList<DefaultRestore> Defaults, IReadOnlyList<(AudioEndpoint Endpoint, int Percent)> Volumes)
    {
        public static AudioRestore Nothing { get; } = new([], []);
    }

    private sealed record AudioStep(AudioEndpoint Endpoint, AudioRoleMask Roles, AudioDirection Direction);
}
