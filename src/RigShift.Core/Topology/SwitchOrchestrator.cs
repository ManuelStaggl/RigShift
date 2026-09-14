using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Runs one profile switch: plan → wait for sleeping targets → apply with retry → audio → confirm → rollback or apps.
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
    private readonly IAppLauncher _apps;
    private readonly IUsbDeviceList _usbDevices;
    private readonly IPowerController _power;
    private readonly IDuckingPreference _ducking;

    /// <summary>
    /// The ducking setting from before a profile with <see cref="Profile.DisableCommunicationsDucking"/> took over; restored
    /// by the next profile without it. Persisted, so it survives a crash or restart (analysis finding B-01).
    /// </summary>
    private readonly IDuckingMemory _duckingMemory;
    private readonly IWindowRescuer _windows;
    private readonly ISwitchConfirmation _confirmation;
    private readonly TopologyPlanner _planner;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public SwitchOrchestrator(
        IDisplayConfigurator display,
        IAudioController audio,
        IAppLauncher apps,
        IUsbDeviceList usbDevices,
        IPowerController power,
        IDuckingPreference ducking,
        IDuckingMemory duckingMemory,
        IWindowRescuer windows,
        ISwitchConfirmation confirmation,
        TopologyPlanner planner,
        SwitchOptions options,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(usbDevices);
        ArgumentNullException.ThrowIfNull(power);
        ArgumentNullException.ThrowIfNull(ducking);
        ArgumentNullException.ThrowIfNull(duckingMemory);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _display = display;
        _audio = audio;
        _apps = apps;
        _usbDevices = usbDevices;
        _power = power;
        _ducking = ducking;
        _duckingMemory = duckingMemory;
        _windows = windows;
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
        AudioRestore audioRestore = confirm
            ? await CaptureAudioAsync(profile.Audio, cancellationToken)
            : AudioRestore.Nothing;
        bool? keepAwakeBefore = confirm ? _power.IsKeepingAwake : null;
        DuckingRestore? duckingRestore = confirm ? await CaptureDuckingAsync(profile, cancellationToken) : null;

        ApplyOutcome applied = await ApplyWithRetryAsync(profile, plan, deadline, cancellationToken);
        if (!applied.Succeeded)
        {
            _log.Error("Switch to {Profile} failed after {Attempts} attempts: {Reason}", profile.Name, applied.Attempts, applied.Message);
            SwitchNote restored = await RestoreAfterFailureAsync(before, cancellationToken);
            return Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = applied.Plan,
                Attempts = applied.Attempts,
                LastNativeError = applied.LastNativeError,
                Message = applied.Message,
                Note = restored,
            }, started);
        }

        plan = applied.Plan;
        AudioOutcome audio = await SwitchAudioAsync(profile.Audio, cancellationToken);

        // With audio, not with apps: both are undone without loss, and the countdown should already run kept awake.
        SwitchKeepAwake(profile);
        await SwitchDuckingAsync(profile, cancellationToken);

        if (confirm)
        {
            ConfirmationResult answer;
            try
            {
                answer = await _confirmation.ConfirmAsync(profile, TimeSpan.FromSeconds(confirmSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                answer = ConfirmationResult.TimedOut;
            }

            if (answer != ConfirmationResult.Confirmed)
            {
                // Cancelled (app exit, logoff): the window closing is no answer, and the rollback must run to the end
                // before the caller learns about the cancellation (analysis finding B-02).
                bool cancelled = cancellationToken.IsCancellationRequested;
                CancellationToken rollbackToken = cancelled ? CancellationToken.None : cancellationToken;
                if (cancelled)
                {
                    _log.Warning("Switch to {Profile} cancelled during confirmation, rolling back", profile.Name);
                }
                else
                {
                    _log.Warning("Switch to {Profile} not confirmed ({Answer}), rolling back", profile.Name, answer);
                }

                RestoreKeepAwake(keepAwakeBefore);
                await RestoreDuckingAsync(duckingRestore, rollbackToken);
                SwitchResult rolledBack = await RollBackAsync(before, audioRestore, plan, applied, audio, answer, started, rollbackToken);
                if (cancelled)
                {
                    _log.Warning("Switch to {Profile} cancelled, rolled back ({Outcome})", profile.Name, rolledBack.Outcome);
                    throw new OperationCanceledException(cancellationToken);
                }

                return rolledBack;
            }
        }

        // Only now: a rejected switch must not have started programs or closed someone's work.
        AppsOutcome apps = await RunAppsAsync(profile, cancellationToken);

        SwitchOutcome outcome = plan.ShouldRetryLater ? SwitchOutcome.AppliedPartially : SwitchOutcome.Applied;
        _log.Information("Switch to {Profile} finished: {Outcome}, audio {Audio}, apps {Apps}, {Attempts} attempts",
            profile.Name, outcome, audio, apps, applied.Attempts);
        return Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Audio = audio,
            Apps = apps,
            Note = applied.UsedDatabaseModes ? SwitchNote.ModesFromDatabase : SwitchNote.None,
        }, started);
    }

    /// <summary>
    /// FollowUp (PLAN 4.3): after a partial switch, a skipped optional display (spacedesk viewer) may appear later.
    /// Re-plans the profile and re-applies the full path set when more displays resolve than were applied.
    /// No confirmation and no audio – the user already accepted this profile. Returns null when nothing changed.
    /// </summary>
    public async Task<SwitchResult?> CatchUpAsync(Profile profile, int appliedDisplays, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
        TopologyPlan plan = _planner.Plan(profile, snapshot);
        if (plan.IsBlocked || plan.Resolved.Count <= appliedDisplays)
        {
            return null;
        }

        long started = _time.GetTimestamp();
        _log.Information("Catching up {Profile}: {Resolved} displays available, {Applied} applied", profile.Name, plan.Resolved.Count, appliedDisplays);
        LogPlan(plan);

        ApplyOutcome applied = await ApplyWithRetryAsync(profile, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken);
        SwitchOutcome outcome = !applied.Succeeded ? SwitchOutcome.Failed
            : applied.Plan.ShouldRetryLater ? SwitchOutcome.AppliedPartially
            : SwitchOutcome.Applied;
        _log.Information("Catch-up of {Profile} finished: {Outcome}, {Attempts} attempts", profile.Name, outcome, applied.Attempts);

        // A failed catch-up can leave displays dark just like a failed switch (analysis finding B-06).
        SwitchNote note = applied.Succeeded
            ? applied.UsedDatabaseModes ? SwitchNote.ModesFromDatabase : SwitchNote.None
            : await RestoreAfterFailureAsync(snapshot, cancellationToken);

        return Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = applied.Plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Message = applied.Message,
            Note = note,
        }, started);
    }

    private async Task<SwitchResult> RollBackAsync(
        DisplaySnapshot before,
        AudioRestore audioRestore,
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
            ? new ApplyOutcome(false, rollbackPlan, 0, null, "None of the previously active displays is available.", false)
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
                Note = SwitchNote.RestoreFailed,
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
            Note = SwitchNote.RestoredPrevious,
            Audio = audio,
        }, started);
    }

    /// <summary>
    /// A failed attempt may leave displays dark (Windows usually reverts on its own, but not reliably). If a display
    /// that was active before is no longer active, re-apply the previous topology.
    /// </summary>
    private async Task<SwitchNote> RestoreAfterFailureAsync(DisplaySnapshot before, CancellationToken cancellationToken)
    {
        DisplaySnapshot now;
        try
        {
            now = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (IsDisplayApiFailure(ex))
        {
            _log.Error(ex, "Restore impossible: the displays could not be queried after the failed switch");
            return SwitchNote.RestoreFailed;
        }

        bool changed = before.Displays
            .Where(d => d.IsActive)
            .Any(d => !now.Displays.Any(n => n.IsActive && n.Identity == d.Identity));
        if (!changed)
        {
            return SwitchNote.None;
        }

        _log.Warning("Previously active displays are dark after the failed switch, restoring the previous topology");
        Profile previous = PreviousTopology(before);
        TopologyPlan plan = _planner.Plan(previous, now);
        LogPlan(plan);
        if (plan.Resolved.Count == 0)
        {
            _log.Error("Restore impossible: none of the previously active displays is available");
            return SwitchNote.RestoreFailed;
        }

        ApplyOutcome restored = await ApplyWithRetryAsync(previous, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken);
        if (!restored.Succeeded)
        {
            _log.Error("Restore after failed switch failed: {Reason}", restored.Message);
            return SwitchNote.RestoreFailed;
        }

        _log.Information("Previous topology restored after failed switch ({Attempts} attempts)", restored.Attempts);
        return SwitchNote.RestoredPrevious;
    }

    /// <summary>What the Windows display layer throws when a query or an apply goes wrong (analysis finding B-07).</summary>
    private static bool IsDisplayApiFailure(Exception ex) =>
        ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;

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
            // A cycle is transient if any attempt in it said "not ready": the database-mode attempt after a 31 may fail
            // with 87 only because the display is still waking up (analysis finding B-08).
            bool transient = false;
            foreach (bool databaseModes in ModeSources)
            {
                if (attempts >= _options.MaxApplyAttempts)
                {
                    return new ApplyOutcome(false, plan, attempts, lastError,
                        string.Create(CultureInfo.InvariantCulture, $"Gave up after {attempts} attempts (last error {lastError})."), false);
                }

                attempts++;
                string modeSource = databaseModes ? "database" : "stored";
                long attemptStarted = _time.GetTimestamp();
                int code;
                try
                {
                    code = await _display.ApplyAsync(plan, new ApplyOptions { UseDatabaseModes = databaseModes }, cancellationToken);
                }
                catch (Exception ex) when (IsDisplayApiFailure(ex))
                {
                    _log.Error(ex, "Attempt {Attempt} threw ({ModeSource} modes)", attempts, modeSource);
                    return new ApplyOutcome(false, plan, attempts, lastError, "The display configuration could not be applied: " + ex.Message, false);
                }

                double milliseconds = _time.GetElapsedTime(attemptStarted).TotalMilliseconds;
                if (code == 0)
                {
                    _log.Information("Attempt {Attempt} succeeded ({ModeSource} modes, {Displays} displays, {Milliseconds:0} ms)",
                        attempts, modeSource, plan.Resolved.Count, milliseconds);
                    await SwitchHdrAsync(profile, cancellationToken);
                    await RescueWindowsAsync(cancellationToken);
                    return new ApplyOutcome(true, plan, attempts, lastError, null, databaseModes);
                }

                lastError = code;
                transient |= code is ErrorGenFailure or ErrorBadConfiguration;
                _log.Warning("Attempt {Attempt} failed with native error {Error} ({ModeSource} modes, {Milliseconds:0} ms)",
                    attempts, code, modeSource, milliseconds);
            }

            if (!transient)
            {
                return new ApplyOutcome(false, plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture, $"SetDisplayConfig failed with error {lastError}."), false);
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return new ApplyOutcome(false, plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture,
                        $"A display did not become ready within {_options.TargetWaitBudget.TotalSeconds} s (error {lastError})."), false);
            }

            try
            {
                plan = await PollTopologyAsync(profile, plan, deadline, afterAttempt: true, cancellationToken);
            }
            catch (Exception ex) when (IsDisplayApiFailure(ex))
            {
                _log.Error(ex, "Displays could not be queried while waiting for a retry");
                return new ApplyOutcome(false, plan, attempts, lastError, "The displays could not be queried: " + ex.Message, false);
            }

            if (BlockReason(plan) is { } blocked)
            {
                return new ApplyOutcome(false, plan, attempts, lastError, blocked, false);
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
                .Select(m => string.Create(CultureInfo.InvariantCulture, $"{DisplayNames.Of(m.Assignment)} ({m.Reason})"));
            return "Required displays are missing: " + string.Join(", ", names);
        }

        return plan.Resolved.Count == 0 ? "None of the profile's displays is available." : null;
    }

    /// <summary>
    /// HDR per display right after the arrangement, on a fresh snapshot: a display that was just switched on only
    /// reports its HDR state once active. Rollbacks restore it the same way, because the previous topology carries the
    /// state it had. Failures are logged and never fail the switch (docs/PLAN.md, section 6, item 10).
    /// </summary>
    private async Task SwitchHdrAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (!profile.Displays.Any(d => d.Hdr is not null))
        {
            return;
        }

        try
        {
            DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
            foreach (DisplayAssignment wanted in profile.Displays)
            {
                if (wanted.Hdr is not { } enabled)
                {
                    continue;
                }

                AttachedDisplay? target = now.Displays.FirstOrDefault(d => d.IsActive
                    && string.Equals(d.Identity.TargetDevicePath, wanted.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase));
                if (target?.ActiveMode is not { } mode)
                {
                    continue;
                }

                if (mode.Hdr is null)
                {
                    _log.Warning("Display {Display} does not support HDR, left unchanged", DisplayNames.Of(wanted));
                }
                else if (mode.Hdr != enabled)
                {
                    int code = await _display.SetHdrAsync(target, enabled, cancellationToken);
                    if (code != 0)
                    {
                        _log.Warning("HDR of {Display} could not be set to {Enabled} (native error {Error})", DisplayNames.Of(wanted), enabled, code);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "HDR for {Profile} could not be set", profile.Name);
        }
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

    private async Task<AppsOutcome> RunAppsAsync(Profile profile, CancellationToken cancellationToken)
    {
        IReadOnlyList<AppAction> apps = profile.Apps;
        if (apps.Count == 0)
        {
            return AppsOutcome.NotConfigured;
        }

        // Wheel software and games want to see the device when they start; without it they start anyway.
        bool deviceMissing = !await WaitForAppsDeviceAsync(profile, cancellationToken);
        AppsOutcome started = await RunAppActionsAsync(apps, cancellationToken);
        return deviceMissing ? AppsOutcome.DeviceMissing : started;
    }

    private async Task<AppsOutcome> RunAppActionsAsync(IReadOnlyList<AppAction> apps, CancellationToken cancellationToken)
    {
        bool complete = true;
        foreach (AppAction app in apps)
        {
            try
            {
                bool running = _apps.IsRunning(app.Path);
                if (app.Kind == AppActionKind.Start && running)
                {
                    _log.Information("App {App} already runs, not started", app.Path);
                    continue;
                }

                if (app.Kind == AppActionKind.Stop && !running)
                {
                    _log.Information("App {App} does not run, nothing to end", app.Path);
                    continue;
                }

                if (app.Kind == AppActionKind.Start)
                {
                    _apps.Start(app.Path, app.Arguments);
                    _log.Information("App {App} started", app.Path);
                }
                else if (await _apps.StopAsync(app.Path, _options.AppStopGrace, cancellationToken))
                {
                    _log.Information("App {App} ended", app.Path);
                }
                else
                {
                    _log.Warning("App {App} could not be ended", app.Path);
                    complete = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "{Action} of app {App} failed", app.Kind, app.Path);
                complete = false;
                continue;
            }

            if (app.WaitSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(app.WaitSeconds), _time, cancellationToken);
            }
        }

        return complete ? AppsOutcome.Applied : AppsOutcome.Incomplete;
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
    /// volume it sets, so a rollback can restore them. The OS reports one default per direction; it is restored for
    /// every role the switch touched.
    /// </summary>
    private async Task<AudioRestore> CaptureAudioAsync(AudioAssignment audio, CancellationToken cancellationToken)
    {
        var defaults = new List<DefaultRestore>();
        foreach (IGrouping<AudioDirection, AudioStep> direction in AudioSteps(audio).GroupBy(s => s.Direction))
        {
            try
            {
                IReadOnlyList<AudioDeviceInfo> devices = await _audio.ListAsync(direction.Key, cancellationToken);
                if (devices.FirstOrDefault(d => d.IsDefault) is { } current)
                {
                    AudioRoleMask roles = direction.Aggregate(AudioRoleMask.None, (mask, step) => mask | step.Roles);
                    defaults.Add(new DefaultRestore(current.Endpoint, roles));
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

    private async Task RestoreAudioAsync(AudioRestore restore, CancellationToken cancellationToken)
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

    /// <summary>
    /// Keep-awake follows the profile (docs/PLAN.md, section 6, item 8). Failures are logged and never fail the switch.
    /// </summary>
    private void SwitchKeepAwake(Profile profile)
    {
        try
        {
            if (_power.IsKeepingAwake != profile.KeepAwake)
            {
                _power.SetKeepAwake(profile.KeepAwake);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Keep-awake for {Profile} could not be set to {KeepAwake}", profile.Name, profile.KeepAwake);
        }
    }

    private void RestoreKeepAwake(bool? keepAwake)
    {
        if (keepAwake is not { } before)
        {
            return;
        }

        try
        {
            if (_power.IsKeepingAwake != before)
            {
                _power.SetKeepAwake(before);
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Restoring keep-awake failed");
        }
    }

    /// <summary>
    /// After every successful apply (switch, rollback, restore, catch-up): windows left on a display that is off now
    /// move to the primary display. Failures are logged and never fail the switch.
    /// </summary>
    private async Task RescueWindowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.WindowRescueDelay, _time, cancellationToken);
            int moved = _windows.RescueOffscreenWindows();
            if (moved > 0)
            {
                _log.Information("Moved {Count} window(s) from displays that are off to the primary display", moved);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Moving windows from displays that are off failed");
        }
    }

    /// <summary>
    /// Polls for the device the profile's apps wait for, up to its wait time. True when it is there or none is set.
    /// </summary>
    private async Task<bool> WaitForAppsDeviceAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId) is not { } deviceId)
        {
            return true;
        }

        int seconds = Profile.ClampAppsWaitSeconds(profile.AppsWaitSeconds);
        string name = profile.AppsWaitForUsbDeviceName ?? deviceId;
        DateTimeOffset deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        long started = _time.GetTimestamp();
        bool waited = false;

        while (!IsUsbDevicePresent(deviceId))
        {
            if (_time.GetUtcNow() >= deadline)
            {
                _log.Warning("Device {Device} did not show up within {Seconds} s, starting apps anyway", name, seconds);
                return false;
            }

            if (!waited)
            {
                _log.Information("Waiting up to {Seconds} s for device {Device} before starting apps", seconds, name);
                waited = true;
            }

            await Task.Delay(_options.DevicePollInterval, _time, cancellationToken);
        }

        if (waited)
        {
            _log.Information("Device {Device} showed up after {Elapsed:0.0} s", name, _time.GetElapsedTime(started).TotalSeconds);
        }

        return true;
    }

    private bool IsUsbDevicePresent(string deviceId)
    {
        try
        {
            return _usbDevices.PresentDeviceIds().Contains(deviceId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed while waiting for {Device}", deviceId);
            return false;
        }
    }

    /// <summary>
    /// A profile with <see cref="Profile.DisableCommunicationsDucking"/> sets "do nothing" during calls; the value from
    /// before the first such profile is remembered and comes back with the next profile without the flag. Failures are
    /// logged and never fail the switch (the registry value is undocumented).
    /// </summary>
    private async Task SwitchDuckingAsync(Profile profile, CancellationToken cancellationToken)
    {
        try
        {
            RememberedDucking? original = await _duckingMemory.LoadAsync(cancellationToken);
            if (profile.DisableCommunicationsDucking)
            {
                int? current = _ducking.Read();
                if (original is null)
                {
                    // Remembered before the registry changes: a crash right after must still find the old value.
                    await _duckingMemory.SaveAsync(current, cancellationToken);
                }

                if (current != CommunicationsDucking.DoNothing)
                {
                    _ducking.Write(CommunicationsDucking.DoNothing);
                    _log.Information("Communications ducking turned off for {Profile} (was {Preference})", profile.Name, current);
                }
            }
            else if (original is not null)
            {
                RestoreRemembered(original, profile.Name);
                await _duckingMemory.ClearAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Communications ducking for {Profile} could not be set", profile.Name);
        }
    }

    /// <summary>
    /// At startup: a remembered value whose profile is no longer active (crash, restart or update in between) comes back
    /// now instead of waiting for the next switch. With a profile that disables ducking still active, it stays remembered.
    /// </summary>
    public async Task RestoreDuckingIfUnusedAsync(Profile? activeProfile, CancellationToken cancellationToken)
    {
        if (activeProfile is { DisableCommunicationsDucking: true })
        {
            return;
        }

        try
        {
            if (await _duckingMemory.LoadAsync(cancellationToken) is not { } original)
            {
                return;
            }

            _log.Information("Remembered communications ducking {Preference} found without an active profile that needs it", original.Value);
            RestoreRemembered(original, activeProfile?.Name ?? "startup");
            await _duckingMemory.ClearAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the remembered communications ducking setting at startup failed");
        }
    }

    private void RestoreRemembered(RememberedDucking original, string profileName)
    {
        if (_ducking.Read() != original.Value)
        {
            _ducking.Write(original.Value);
            _log.Information("Communications ducking {Preference} from before restored for {Profile}", original.Value, profileName);
        }
    }

    private async Task<DuckingRestore?> CaptureDuckingAsync(Profile profile, CancellationToken cancellationToken)
    {
        try
        {
            RememberedDucking? remembered = await _duckingMemory.LoadAsync(cancellationToken);
            return !profile.DisableCommunicationsDucking && remembered is null
                ? null
                : new DuckingRestore(_ducking.Read(), remembered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Could not read the communications ducking setting; rollback will leave it unchanged");
            return null;
        }
    }

    private async Task RestoreDuckingAsync(DuckingRestore? restore, CancellationToken cancellationToken)
    {
        if (restore is null)
        {
            return;
        }

        try
        {
            if (_ducking.Read() != restore.Value)
            {
                _ducking.Write(restore.Value);
            }

            if (restore.BeforeProfiles is { } remembered)
            {
                await _duckingMemory.SaveAsync(remembered.Value, cancellationToken);
            }
            else
            {
                await _duckingMemory.ClearAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the communications ducking setting failed");
        }
    }

    private void LogPlan(TopologyPlan plan)
    {
        _log.Information("Plan for {Profile}: {Resolved} resolved, {Missing} missing, {Warnings} warnings",
            plan.Profile.Name, plan.Resolved.Count, plan.Missing.Count, plan.Warnings.Count);
        foreach (MissingDisplay missing in plan.Missing)
        {
            _log.Warning("Display {Display} missing: {Reason} (optional: {Optional})",
                DisplayNames.Of(missing.Assignment), missing.Reason, missing.Assignment.IsOptional);
        }

        foreach (PlanWarning warning in plan.Warnings)
        {
            _log.Warning("{WarningKind}: {WarningMessage}", warning.Kind, warning.Message);
        }
    }

    private SwitchResult Finish(SwitchResult result, long started) =>
        result with { Duration = _time.GetElapsedTime(started) };

    private sealed record ApplyOutcome(bool Succeeded, TopologyPlan Plan, int Attempts, int? LastNativeError, string? Message, bool UsedDatabaseModes);

    private sealed record DefaultRestore(AudioEndpoint Endpoint, AudioRoleMask Roles);

    private sealed record AudioRestore(IReadOnlyList<DefaultRestore> Defaults, IReadOnlyList<(AudioEndpoint Endpoint, int Percent)> Volumes)
    {
        public static AudioRestore Nothing { get; } = new([], []);
    }

    private sealed record AudioStep(AudioEndpoint Endpoint, AudioRoleMask Roles, AudioDirection Direction);

    private sealed record DuckingRestore(int? Value, RememberedDucking? BeforeProfiles);
}
