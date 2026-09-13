using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Runs one profile switch: plan → wait for sleeping targets → apply with retry → audio → confirm → rollback.
/// State machine and rationale: docs/PLAN.md section 4.3; hard rules: docs/display-topology.md.
/// </summary>
public sealed class SwitchOrchestrator
{
    /// <summary>ERROR_GEN_FAILURE. In practice "target not ready yet", not "impossible" (display-topology.md, rule 4).</summary>
    public const int ErrorGenFailure = 31;

    /// <summary>
    /// ERROR_BAD_CONFIGURATION. Observed on the gaming PC (M5, 2026-09-13) when a sleeping ultrawide dropped off the bus
    /// mid-switch and reappeared seconds later – as transient as error 31.
    /// </summary>
    public const int ErrorBadConfiguration = 1610;

    /// <summary>Per retry cycle: stored modes first, then database modes (display-topology.md, rule 5).</summary>
    private static readonly bool[] ModeSources = [false, true];

    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly ISwitchConfirmation _confirmation;
    private readonly TopologyPlanner _planner;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public SwitchOrchestrator(
        IDisplayConfigurator display,
        IAudioController audio,
        ISwitchConfirmation confirmation,
        TopologyPlanner planner,
        SwitchOptions options,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _display = display;
        _audio = audio;
        _confirmation = confirmation;
        _planner = planner;
        _options = options;
        _time = time;
        _log = log.ForContext<SwitchOrchestrator>();
    }

    public async Task<SwitchResult> SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        long started = _time.GetTimestamp();
        _log.Information("Switching to profile {Profile} (dry run: {DryRun}, skip confirmation: {SkipConfirmation})",
            profile.Name, request.DryRun, request.SkipConfirmation);

        DisplaySnapshot before = await _display.QueryAsync(cancellationToken);
        TopologyPlan plan = _planner.Plan(profile, before);
        LogPlan(plan);

        if (request.DryRun)
        {
            return Finish(new SwitchResult { Outcome = SwitchOutcome.DryRun, Plan = plan }, started);
        }

        DateTimeOffset deadline = _time.GetUtcNow() + _options.TargetWaitBudget;
        plan = await PollTopologyAsync(profile, plan, deadline, afterAttempt: false, cancellationToken);
        if (BlockReason(plan) is { } blocked)
        {
            _log.Warning("Switch to {Profile} blocked: {Reason}", profile.Name, blocked);
            return Finish(new SwitchResult { Outcome = SwitchOutcome.Blocked, Plan = plan, Message = blocked }, started);
        }

        int confirmSeconds = profile.ConfirmTimeoutSeconds ?? request.DefaultConfirmTimeoutSeconds;
        bool confirm = confirmSeconds > 0 && !request.SkipConfirmation;
        IReadOnlyList<AudioRestore> audioRestore = confirm
            ? await CaptureAudioDefaultsAsync(profile.Audio, cancellationToken)
            : [];

        ApplyOutcome applied = await ApplyWithRetryAsync(profile, plan, deadline, cancellationToken);
        if (!applied.Succeeded)
        {
            _log.Error("Switch to {Profile} failed after {Attempts} attempts: {Reason}", profile.Name, applied.Attempts, applied.Message);
            string? restored = await RestoreAfterFailureAsync(before, cancellationToken);
            return Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = applied.Plan,
                Attempts = applied.Attempts,
                LastNativeError = applied.LastNativeError,
                Message = restored is null ? applied.Message : applied.Message + " " + restored,
            }, started);
        }

        plan = applied.Plan;
        AudioOutcome audio = await SwitchAudioAsync(profile.Audio, cancellationToken);

        if (confirm)
        {
            ConfirmationResult answer = await _confirmation.ConfirmAsync(
                profile, TimeSpan.FromSeconds(confirmSeconds), cancellationToken);
            if (answer != ConfirmationResult.Confirmed)
            {
                _log.Warning("Switch to {Profile} not confirmed ({Answer}), rolling back", profile.Name, answer);
                return await RollBackAsync(before, audioRestore, plan, applied, audio, answer, started, cancellationToken);
            }
        }

        SwitchOutcome outcome = plan.ShouldRetryLater ? SwitchOutcome.AppliedPartially : SwitchOutcome.Applied;
        _log.Information("Switch to {Profile} finished: {Outcome}, audio {Audio}, {Attempts} attempts",
            profile.Name, outcome, audio, applied.Attempts);
        return Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Audio = audio,
        }, started);
    }

    private async Task<SwitchResult> RollBackAsync(
        DisplaySnapshot before,
        IReadOnlyList<AudioRestore> audioRestore,
        TopologyPlan plan,
        ApplyOutcome applied,
        AudioOutcome audio,
        ConfirmationResult answer,
        long started,
        CancellationToken cancellationToken)
    {
        Profile previous = PreviousTopology(before);
        DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
        TopologyPlan rollbackPlan = _planner.Plan(previous, now);
        LogPlan(rollbackPlan);

        ApplyOutcome rolledBack = rollbackPlan.Resolved.Count == 0
            ? new ApplyOutcome(false, rollbackPlan, 0, null, "None of the previously active displays is available.")
            : await ApplyWithRetryAsync(previous, rollbackPlan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken);

        await RestoreAudioAsync(audioRestore, cancellationToken);

        if (!rolledBack.Succeeded)
        {
            string message = string.Create(CultureInfo.InvariantCulture,
                $"Switch was not confirmed ({answer}) and restoring the previous topology failed: {rolledBack.Message}");
            _log.Error("Rollback failed: {Reason}", rolledBack.Message);
            return Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = plan,
                Attempts = applied.Attempts + rolledBack.Attempts,
                LastNativeError = rolledBack.LastNativeError,
                Message = message,
                Audio = audio,
            }, started);
        }

        _log.Information("Previous topology restored after {Attempts} attempts", rolledBack.Attempts);
        return Finish(new SwitchResult
        {
            Outcome = SwitchOutcome.RolledBack,
            Plan = plan,
            Attempts = applied.Attempts + rolledBack.Attempts,
            LastNativeError = applied.LastNativeError,
            Message = string.Create(CultureInfo.InvariantCulture, $"Not confirmed ({answer}); previous topology restored."),
            Audio = audio,
        }, started);
    }

    /// <summary>
    /// A failed attempt may leave displays dark (Windows usually reverts on its own, but not reliably). If a display
    /// that was active before is no longer active, re-apply the previous topology. Returns a note for the result message.
    /// </summary>
    private async Task<string?> RestoreAfterFailureAsync(DisplaySnapshot before, CancellationToken cancellationToken)
    {
        DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
        bool changed = before.Displays
            .Where(d => d.IsActive)
            .Any(d => !now.Displays.Any(n => n.IsActive && n.Identity == d.Identity));
        if (!changed)
        {
            return null;
        }

        _log.Warning("Previously active displays are dark after the failed switch, restoring the previous topology");
        Profile previous = PreviousTopology(before);
        TopologyPlan plan = _planner.Plan(previous, now);
        LogPlan(plan);
        if (plan.Resolved.Count == 0)
        {
            _log.Error("Restore impossible: none of the previously active displays is available");
            return "Restoring the previous topology failed.";
        }

        ApplyOutcome restored = await ApplyWithRetryAsync(previous, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken);
        if (!restored.Succeeded)
        {
            _log.Error("Restore after failed switch failed: {Reason}", restored.Message);
            return "Restoring the previous topology failed.";
        }

        _log.Information("Previous topology restored after failed switch ({Attempts} attempts)", restored.Attempts);
        return "Previous topology restored.";
    }

    /// <summary>
    /// Stored modes first, then database modes (rule 5). On a transient error (31, 1610): wait, re-query, re-plan and try again
    /// within the time budget (rule 4). Every retry uses a fresh snapshot because LUIDs may change (rule 2).
    /// </summary>
    private async Task<ApplyOutcome> ApplyWithRetryAsync(Profile profile, TopologyPlan plan, DateTimeOffset deadline, CancellationToken cancellationToken)
    {
        int attempts = 0;
        int? lastError = null;

        while (true)
        {
            foreach (bool databaseModes in ModeSources)
            {
                if (attempts >= _options.MaxApplyAttempts)
                {
                    return new ApplyOutcome(false, plan, attempts, lastError,
                        string.Create(CultureInfo.InvariantCulture, $"Gave up after {attempts} attempts (last error {lastError})."));
                }

                attempts++;
                int code = await _display.ApplyAsync(plan, new ApplyOptions { UseDatabaseModes = databaseModes }, cancellationToken);
                if (code == 0)
                {
                    _log.Information("Attempt {Attempt} succeeded ({ModeSource} modes, {Displays} displays)",
                        attempts, databaseModes ? "database" : "stored", plan.Resolved.Count);
                    return new ApplyOutcome(true, plan, attempts, lastError, null);
                }

                lastError = code;
                _log.Warning("Attempt {Attempt} failed with native error {Error} ({ModeSource} modes)",
                    attempts, code, databaseModes ? "database" : "stored");
            }

            if (lastError is not (ErrorGenFailure or ErrorBadConfiguration))
            {
                return new ApplyOutcome(false, plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture, $"SetDisplayConfig failed with error {lastError}."));
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return new ApplyOutcome(false, plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture,
                        $"A display did not become ready within {_options.TargetWaitBudget.TotalSeconds} s (error {lastError})."));
            }

            plan = await PollTopologyAsync(profile, plan, deadline, afterAttempt: true, cancellationToken);
            if (BlockReason(plan) is { } blocked)
            {
                return new ApplyOutcome(false, plan, attempts, lastError, blocked);
            }
        }
    }

    /// <summary>
    /// Re-queries the topology while a required display is attached but not ready, until the deadline. After a failed
    /// attempt a required display that vanished is waited for too: a waking monitor can drop off the bus for seconds.
    /// </summary>
    private async Task<TopologyPlan> PollTopologyAsync(
        Profile profile, TopologyPlan plan, DateTimeOffset deadline, bool afterAttempt, CancellationToken cancellationToken)
    {
        bool force = afterAttempt;
        while ((force || IsWaitingForTarget(plan, afterAttempt)) && _time.GetUtcNow() < deadline)
        {
            force = false;
            await Task.Delay(_options.PollInterval, _time, cancellationToken);
            DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
            plan = _planner.Plan(profile, snapshot);
            _log.Debug("Re-planned {Profile}: {Resolved} resolved, {Missing} missing", profile.Name, plan.Resolved.Count, plan.Missing.Count);
        }

        return plan;
    }

    // After an attempt: wait for vanished required displays, and for optional ones when nothing else is left to apply –
    // the rollback profile marks every display optional (M5 log 2026-09-13 19:59: rollback to a G9 that fell asleep).
    private static bool IsWaitingForTarget(TopologyPlan plan, bool includeDetached) =>
        includeDetached
            ? plan.Missing.Any(m => !m.Assignment.IsOptional) || (plan.Resolved.Count == 0 && plan.Missing.Count > 0)
            : plan.Missing.Any(m => !m.Assignment.IsOptional && m.Reason == MissingReason.AttachedButUnavailable)
              && !plan.Missing.Any(m => !m.Assignment.IsOptional && m.Reason == MissingReason.NotAttached);

    private static string? BlockReason(TopologyPlan plan)
    {
        if (plan.IsBlocked)
        {
            IEnumerable<string> names = plan.Missing
                .Where(m => !m.Assignment.IsOptional)
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"{DisplayNames.Of(m.Assignment.Identity)} ({m.Reason})"));
            return "Required displays are missing: " + string.Join(", ", names);
        }

        return plan.Resolved.Count == 0 ? "None of the profile's displays is available." : null;
    }

    /// <summary>The topology before the switch, as a throwaway profile. All displays optional: a partial restore beats none.</summary>
    private static Profile PreviousTopology(DisplaySnapshot before)
    {
        var displays = new List<DisplayAssignment>();
        foreach (AttachedDisplay display in before.Displays)
        {
            if (display.IsActive && display.ActiveMode is { } mode)
            {
                displays.Add(mode with { Identity = display.Identity, IsOptional = true });
            }
        }

        return new Profile
        {
            Id = Guid.Empty,
            Name = "Previous topology",
            Displays = displays,
            ConfirmTimeoutSeconds = 0,
        };
    }

    private async Task<AudioOutcome> SwitchAudioAsync(AudioAssignment audio, CancellationToken cancellationToken)
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

        if (audio.Playback is { } playback && audio.PlaybackVolumePercent is { } volume)
        {
            try
            {
                await _audio.SetVolumeAsync(playback, volume, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Setting volume of {Device} to {Volume} % failed", playback.FriendlyName, volume);
                complete = false;
            }
        }

        return complete ? AudioOutcome.Applied : AudioOutcome.Incomplete;
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
    /// Remembers the current default device per direction the profile changes, so a rollback can restore it.
    /// The OS reports one default per direction; it is restored for every role the switch touched.
    /// </summary>
    private async Task<IReadOnlyList<AudioRestore>> CaptureAudioDefaultsAsync(AudioAssignment audio, CancellationToken cancellationToken)
    {
        var restore = new List<AudioRestore>();
        foreach (IGrouping<AudioDirection, AudioStep> direction in AudioSteps(audio).GroupBy(s => s.Direction))
        {
            try
            {
                IReadOnlyList<AudioDeviceInfo> devices = await _audio.ListAsync(direction.Key, cancellationToken);
                if (devices.FirstOrDefault(d => d.IsDefault) is { } current)
                {
                    AudioRoleMask roles = direction.Aggregate(AudioRoleMask.None, (mask, step) => mask | step.Roles);
                    restore.Add(new AudioRestore(current.Endpoint, roles));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "Could not read current {Direction} default; rollback will leave audio unchanged", direction.Key);
            }
        }

        return restore;
    }

    private async Task RestoreAudioAsync(IReadOnlyList<AudioRestore> restore, CancellationToken cancellationToken)
    {
        foreach (AudioRestore item in restore)
        {
            await TrySetDefaultAsync(item.Endpoint, item.Roles, cancellationToken);
        }
    }

    private void LogPlan(TopologyPlan plan)
    {
        _log.Information("Plan for {Profile}: {Resolved} resolved, {Missing} missing, {Warnings} warnings",
            plan.Profile.Name, plan.Resolved.Count, plan.Missing.Count, plan.Warnings.Count);
        foreach (MissingDisplay missing in plan.Missing)
        {
            _log.Warning("Display {Display} missing: {Reason} (optional: {Optional})",
                DisplayNames.Of(missing.Assignment.Identity), missing.Reason, missing.Assignment.IsOptional);
        }

        foreach (PlanWarning warning in plan.Warnings)
        {
            _log.Warning("{WarningKind}: {WarningMessage}", warning.Kind, warning.Message);
        }
    }

    private SwitchResult Finish(SwitchResult result, long started) =>
        result with { Duration = _time.GetElapsedTime(started) };

    private sealed record ApplyOutcome(bool Succeeded, TopologyPlan Plan, int Attempts, int? LastNativeError, string? Message);

    private sealed record AudioRestore(AudioEndpoint Endpoint, AudioRoleMask Roles);

    private sealed record AudioStep(AudioEndpoint Endpoint, AudioRoleMask Roles, AudioDirection Direction);
}
