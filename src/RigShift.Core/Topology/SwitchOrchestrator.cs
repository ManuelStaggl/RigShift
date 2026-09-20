using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Runs one profile switch: plan → wait for sleeping targets → apply with retry → audio → confirm → rollback or apps.
/// State machine of a switch; the hard rules are in docs/display-topology.md.
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
    private readonly IPowerController _power;
    private readonly IWindowRescuer _windows;
    private readonly IDesktopIcons _desktopIcons;
    private readonly ISwitchConfirmation _confirmation;
    private readonly TopologyPlanner _planner;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ISwitchJournal _journal;
    private readonly AudioSwitcher _audioSwitcher;
    private readonly AppRunner _appRunner;
    private readonly DuckingSwitcher _duckingSwitcher;
    private readonly SurroundSwitcher _surroundSwitcher;

    public SwitchOrchestrator(
        IDisplayConfigurator display,
        IAudioController audio,
        IAppLauncher apps,
        IUsbDeviceList usbDevices,
        IPowerController power,
        IDuckingPreference ducking,
        IDuckingMemory duckingMemory,
        IWindowRescuer windows,
        IDesktopIcons desktopIcons,
        ISurroundController surround,
        ISwitchConfirmation confirmation,
        ISwitchJournal journal,
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
        ArgumentNullException.ThrowIfNull(desktopIcons);
        ArgumentNullException.ThrowIfNull(surround);
        ArgumentNullException.ThrowIfNull(confirmation);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(planner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _display = display;
        _power = power;
        _windows = windows;
        _desktopIcons = desktopIcons;
        _confirmation = confirmation;
        _journal = journal;
        _planner = planner;
        _options = options;
        _time = time;
        _log = log.ForContext<SwitchOrchestrator>();
        _audioSwitcher = new AudioSwitcher(audio, _log);
        _appRunner = new AppRunner(apps, usbDevices, options, time, _log);
        _duckingSwitcher = new DuckingSwitcher(ducking, duckingMemory, _log);
        _surroundSwitcher = new SurroundSwitcher(surround, _log);
    }

    /// <summary>
    /// Raised on the switch's thread when required displays are not connected and the switch waits for the user to switch
    /// them on (finding HW-16). Carries those displays.
    /// </summary>
    public event EventHandler<IReadOnlyList<DisplayAssignment>>? WaitingForDisplays;

    public async Task<SwitchResult> SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        if (request.DryRun)
        {
            return await CheckAsync(profile, cancellationToken);
        }

        if (request.KeepDisplays)
        {
            return await ApplyRestAsync(profile, cancellationToken);
        }

        long started = _time.GetTimestamp();
        _log.Information("Switching to profile {Profile} (skip confirmation: {SkipConfirmation}, from link: {FromLink})",
            profile.Name, request.SkipConfirmation, request.FromLink);

        // A new switch ends the apps of the previous one, e.g. still waiting for their device (analysis finding B-03).
        _ = CancelPendingAppsAsync();

        DisplaySnapshot before = await _display.QueryAsync(cancellationToken);
        SurroundSetting? surroundBefore = await _surroundSwitcher.CaptureAsync(profile, cancellationToken);
        DisplaySnapshot planFrom = before;
        bool recorded = false;

        // Record the way back before the first change, not after: the crash this guards against can happen in between
        // (plan point 22). A switch that never gets that far leaves no record at all.
        async Task RecordAsync(CancellationToken token)
        {
            if (!recorded)
            {
                recorded = true;
                await _journal.BeginAsync(
                    new InterruptedSwitch
                    {
                        Previous = PreviousTopology(before) with { Surround = surroundBefore },
                        TargetProfileName = profile.Name,
                        StartedUtc = _time.GetUtcNow(),
                    },
                    token);
            }
        }

        // Surround first: switching it on or off turns several monitors into one wide one and back, so it decides which
        // displays the arrangement can address at all - planning before it would plan against displays about to vanish.
        SurroundApplyResult surround = SurroundApplyResult.NotConfigured;
        if (profile.Surround is not null)
        {
            await RecordAsync(cancellationToken);
            surround = await _surroundSwitcher.SwitchAsync(profile, cancellationToken);
            if (surround.Outcome == SurroundOutcome.Failed)
            {
                // Nothing else was touched yet, so this is a block, not a half-finished switch.
                return await Finish(
                    new SwitchResult
                    {
                        Outcome = SwitchOutcome.Blocked,
                        Plan = _planner.Plan(profile, before),
                        Surround = surround.Outcome,
                        Message = surround.Message,
                    },
                    started);
            }

            if (surround.Outcome == SurroundOutcome.Changed)
            {
                // Plan against what exists now. `before` stays the state to return to, which is the one before Surround.
                planFrom = await _display.QueryAsync(cancellationToken);
            }
        }

        TopologyPlan plan = _planner.Plan(profile, planFrom);
        LogPlan(plan);

        DateTimeOffset deadline = _time.GetUtcNow() + _options.TargetWaitBudget;
        List<DisplayAssignment> notConnected = [.. plan.Missing.Where(m => !m.Assignment.IsOptional && m.Reason == MissingReason.NotAttached).Select(m => m.Assignment)];
        if (notConnected.Count > 0)
        {
            // A monitor that left the bus cannot be woken by software (no CEC on GPUs, DDC/CI needs the link): ask the user
            // to switch it on and wait for it instead of blocking at once (finding HW-16).
            _log.Information("Waiting up to {Seconds:0} s for required displays that are not connected: {Displays}",
                _options.MissingDisplayWaitBudget.TotalSeconds, string.Join(", ", notConnected.Select(d => DisplayNames.Of(d))));
            deadline = _time.GetUtcNow() + _options.MissingDisplayWaitBudget;
            WaitingForDisplays?.Invoke(this, notConnected);
        }

        plan = await PollTopologyAsync(profile, plan, deadline, force: false, includeDetached: notConnected.Count > 0, cancellationToken);
        if (BlockReason(plan) is { } blocked)
        {
            _log.Warning("Switch to {Profile} blocked: {Reason}", profile.Name, blocked);
            await _surroundSwitcher.RestoreAsync(surroundBefore, cancellationToken);
            return await Finish(
                new SwitchResult { Outcome = SwitchOutcome.Blocked, Plan = plan, Surround = surround.Outcome, Message = blocked },
                started);
        }

        // Waiting for a sleeping display may have used up most of the budget; a display that wakes late and then answers
        // 31 still needs time for its retries (analysis finding B-09).
        deadline = _time.GetUtcNow() + _options.TargetWaitBudget;

        int confirmSeconds = request.DefaultConfirmTimeoutSeconds;
        // A link may come from a web page: it always asks, at least with the default timeout (analysis finding H-02).
        bool confirm = request.FromLink || (confirmSeconds > 0 && !profile.SwitchWithoutAsking && !request.SkipConfirmation);
        if (request.FromLink && confirmSeconds <= 0)
        {
            confirmSeconds = (int)SwitchOptions.DefaultConfirmTimeout.TotalSeconds;
        }

        AudioSwitcher.AudioRestore audioRestore = confirm
            ? await _audioSwitcher.CaptureAsync(profile.Audio, cancellationToken)
            : AudioSwitcher.AudioRestore.Nothing;
        bool? keepAwakeBefore = confirm ? _power.IsKeepingAwake : null;
        DuckingSwitcher.DuckingRestore? duckingRestore = confirm ? await _duckingSwitcher.CaptureAsync(profile, cancellationToken) : null;

        await RecordAsync(cancellationToken);
        ApplyOutcome applied = await ApplyWithRetryAsync(profile, plan, deadline, cancellationToken);
        if (!applied.Succeeded)
        {
            _log.Error("Switch to {Profile} failed after {Attempts} attempts: {Reason}", profile.Name, applied.Attempts, applied.Message);
            SwitchNote restored = await RestoreAfterFailureAsync(before, surroundBefore, cancellationToken);
            return await Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = applied.Plan,
                Attempts = applied.Attempts,
                LastNativeError = applied.LastNativeError,
                Message = applied.Message,
                Surround = surround.Outcome,
                Note = restored,
            }, started);
        }

        plan = applied.Plan;
        ModeCheck modes = ModeCheck.AsPlanned(plan);
        AudioOutcome audio = AudioOutcome.NotConfigured;
        ConfirmationResult answer = ConfirmationResult.Confirmed;
        long askedAt = 0;
        Exception? thrown = null;

        // From here until the answer the screens show an arrangement nobody confirmed, possibly on displays the user
        // cannot see. Whatever is thrown in between – a dialog that cannot be shown, a COM surprise, the app exiting –
        // must end in the way back, never in that arrangement staying.
        try
        {
            modes = applied.UsedDatabaseModes ? await CheckDatabaseModesAsync(plan, cancellationToken) : modes;
            plan = modes.Plan;
            long audioStarted = _time.GetTimestamp();
            audio = await _audioSwitcher.SwitchAsync(profile.Audio, cancellationToken);
            if (audio != AudioOutcome.NotConfigured)
            {
                _log.Information("Audio for {Profile}: {Audio} after {Milliseconds:0} ms", profile.Name, audio, _time.GetElapsedTime(audioStarted).TotalMilliseconds);
            }

            // With audio, not with apps: both are undone without loss, and the countdown should already run kept awake.
            SwitchKeepAwake(profile);
            await _duckingSwitcher.SwitchAsync(profile, cancellationToken);

            if (confirm)
            {
                askedAt = _time.GetTimestamp();
                answer = await _confirmation.ConfirmAsync(profile, before, TimeSpan.FromSeconds(confirmSeconds), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (confirm && cancellationToken.IsCancellationRequested)
        {
            answer = ConfirmationResult.Cancelled;
        }
        catch (Exception ex) when (confirm)
        {
            _log.Error(ex, "Switch to {Profile} threw before it was confirmed, rolling back", profile.Name);
            thrown = ex;
            answer = ConfirmationResult.Cancelled;
        }

        if (confirm)
        {
            if (answer == ConfirmationResult.Confirmed)
            {
                _log.Information("Switch to {Profile} confirmed after {Seconds:0.0} s", profile.Name, _time.GetElapsedTime(askedAt).TotalSeconds);
            }

            if (answer != ConfirmationResult.Confirmed)
            {
                // Cancelled (app exit, logoff): the window closing is no answer, and the rollback must run to the end
                // before the caller learns about the cancellation (analysis finding B-02).
                bool cancelled = thrown is null && cancellationToken.IsCancellationRequested;
                CancellationToken rollbackToken = cancelled || thrown is not null ? CancellationToken.None : cancellationToken;
                if (cancelled)
                {
                    _log.Warning("Switch to {Profile} cancelled during confirmation, rolling back", profile.Name);
                }
                else if (thrown is null)
                {
                    _log.Warning("Switch to {Profile} not confirmed ({Answer}), rolling back", profile.Name, answer);
                }

                RestoreKeepAwake(keepAwakeBefore);
                try
                {
                    await _duckingSwitcher.RestoreAsync(duckingRestore, rollbackToken);

                    // Surround comes back first: while the wrong one runs, the displays of the old arrangement do not exist.
                    await _surroundSwitcher.RestoreAsync(surroundBefore, rollbackToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The displays matter most: nothing on the way there may keep the rollback from reaching them.
                    _log.Error(ex, "Restoring ducking or Surround threw, the displays are rolled back regardless");
                }

                SwitchResult rolledBack = await RollBackAsync(before, audioRestore, plan, applied, audio, answer, started, rollbackToken);
                if (cancelled)
                {
                    _log.Warning("Switch to {Profile} cancelled, rolled back ({Outcome})", profile.Name, rolledBack.Outcome);
                    throw new OperationCanceledException(cancellationToken);
                }

                if (thrown is not null)
                {
                    return rolledBack with
                    {
                        Outcome = SwitchOutcome.Failed,
                        Message = string.Create(CultureInfo.InvariantCulture, $"{thrown.Message} {rolledBack.Message}"),
                    };
                }

                return rolledBack;
            }
        }

        // Windows move only once the switch stays: a rejected one would leave them moved without a way back (B-14).
        await RescueWindowsAsync(cancellationToken);
        await RestoreDesktopIconsAsync(profile, cancellationToken);

        // Only now: a rejected switch must not have started programs or closed someone's work. The apps run after the
        // result, so waiting for their device holds up neither hotkeys nor automation nor the next switch (B-03).
        Task<AppsOutcome> appsRun = _appRunner.Start(profile);
        AppsOutcome apps = profile.Apps.Count == 0 ? AppsOutcome.NotConfigured : AppsOutcome.Pending;

        // A missing optional display (spacedesk viewer) is no partial switch; the catch-up follows it (finding HW-03).
        SwitchOutcome outcome = modes.DisplaysDark ? SwitchOutcome.AppliedPartially : SwitchOutcome.Applied;
        _log.Information("Switch to {Profile} finished: {Outcome}, audio {Audio}, apps {Apps}, {Attempts} attempts, {Seconds:0.0} s",
            profile.Name, outcome, audio, apps, applied.Attempts, _time.GetElapsedTime(started).TotalSeconds);
        return await Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Audio = audio,
            Apps = apps,
            AppsCompletion = appsRun,
            Surround = surround.Outcome,
            Note = modes.Note,
        }, started);
    }

    /// <summary>
    /// Windows restored the profile's display layout on its own, e.g. when a monitor was switched on (finding HW-15): the
    /// rest of the profile follows without a display change and without asking – the user chose it from the notification.
    /// </summary>
    private async Task<SwitchResult> ApplyRestAsync(Profile profile, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        _log.Information("Applying the rest of profile {Profile}; Windows already restored its displays", profile.Name);
        _ = CancelPendingAppsAsync();

        TopologyPlan plan = _planner.Plan(profile, await _display.QueryAsync(cancellationToken));
        AudioOutcome audio = await _audioSwitcher.SwitchAsync(profile.Audio, cancellationToken);
        SwitchKeepAwake(profile);
        await _duckingSwitcher.SwitchAsync(profile, cancellationToken);
        await RescueWindowsAsync(cancellationToken);

        Task<AppsOutcome> appsRun = _appRunner.Start(profile);
        AppsOutcome apps = profile.Apps.Count == 0 ? AppsOutcome.NotConfigured : AppsOutcome.Pending;
        _log.Information("Rest of {Profile} applied: audio {Audio}, apps {Apps}", profile.Name, audio, apps);
        return await Finish(new SwitchResult { Outcome = SwitchOutcome.Applied, Plan = plan, Audio = audio, Apps = apps, AppsCompletion = appsRun }, started);
    }

    /// <summary>
    /// Dry run: plans against the live topology without touching anything. Needs no exclusive access, so it may run while
    /// a switch runs (analysis finding B-13).
    /// </summary>
    public async Task<SwitchResult> CheckAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);

        long started = _time.GetTimestamp();
        _log.Information("Checking profile {Profile} (dry run)", profile.Name);
        DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
        TopologyPlan plan = _planner.Plan(profile, snapshot);
        LogPlan(plan);
        _log.Information("Dry run of {Profile} finished in {Milliseconds:0} ms", profile.Name, _time.GetElapsedTime(started).TotalMilliseconds);
        return await Finish(new SwitchResult { Outcome = SwitchOutcome.DryRun, Plan = plan }, started);
    }

    /// <summary>
    /// Cancels the apps of an earlier switch that still run or wait for their device, and completes once they ended.
    /// The cancellation is requested before this method first yields.
    /// </summary>
    public Task CancelPendingAppsAsync() => _appRunner.CancelPendingAsync();

    /// <summary>
    /// With database modes Windows decides the modes and may even leave a display dark; the stored plan says nothing
    /// about that. Compares a fresh snapshot with the plan (analysis finding B-11): dark displays count as missing,
    /// the note only stays when a mode differs.
    /// </summary>
    private async Task<ModeCheck> CheckDatabaseModesAsync(TopologyPlan plan, CancellationToken cancellationToken)
    {
        DisplaySnapshot now;
        try
        {
            now = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (IsDisplayApiFailure(ex))
        {
            _log.Warning(ex, "Displays could not be queried to check the database modes");
            return new ModeCheck(plan, SwitchNote.ModesFromDatabase, DisplaysDark: false);
        }

        var dark = new List<PlannedDisplay>();
        bool modesDiffer = false;
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            AttachedDisplay? live = now.Displays.FirstOrDefault(d => d.IsActive
                && string.Equals(d.Identity.TargetDevicePath, planned.Target.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase));
            DisplayAssignment wanted = planned.Assignment;
            if (live?.ActiveMode is not { } mode)
            {
                _log.Warning("Display {Display} stayed dark after applying with database modes", DisplayNames.Of(wanted));
                dark.Add(planned);
            }
            else if (mode.Width != wanted.Width || mode.Height != wanted.Height || Math.Abs(Hertz(mode) - Hertz(wanted)) >= 0.5)
            {
                _log.Warning("Display {Display} runs {Width}x{Height} at {Hertz:0.##} Hz instead of {WantedWidth}x{WantedHeight} at {WantedHertz:0.##} Hz",
                    DisplayNames.Of(wanted), mode.Width, mode.Height, Hertz(mode), wanted.Width, wanted.Height, Hertz(wanted));
                modesDiffer = true;
            }
        }

        if (dark.Count == 0)
        {
            _log.Information("Database modes checked: {Result}", modesDiffer ? "modes differ from the profile" : "as planned");
            return new ModeCheck(plan, modesDiffer ? SwitchNote.ModesFromDatabase : SwitchNote.None, DisplaysDark: false);
        }

        TopologyPlan partial = plan with
        {
            Resolved = plan.Resolved.Except(dark).ToList(),
            Missing = [.. plan.Missing, .. dark.Select(d => new MissingDisplay(d.Assignment, MissingReason.AttachedButUnavailable))],
        };
        return new ModeCheck(partial, SwitchNote.ModesFromDatabase, DisplaysDark: true);
    }

    private static double Hertz(DisplayAssignment mode) => RefreshRate.Of(mode).Hertz;

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
        if (applied.Succeeded)
        {
            await RescueWindowsAsync(cancellationToken);
        }

        ModeCheck modes = applied.Succeeded && applied.UsedDatabaseModes
            ? await CheckDatabaseModesAsync(applied.Plan, cancellationToken)
            : ModeCheck.AsPlanned(applied.Plan);
        SwitchOutcome outcome = !applied.Succeeded ? SwitchOutcome.Failed
            : modes.DisplaysDark ? SwitchOutcome.AppliedPartially
            : SwitchOutcome.Applied;
        _log.Information("Catch-up of {Profile} finished: {Outcome}, {Attempts} attempts, {Seconds:0.0} s",
            profile.Name, outcome, applied.Attempts, _time.GetElapsedTime(started).TotalSeconds);

        // A failed catch-up can leave displays dark just like a failed switch (analysis finding B-06).
        SwitchNote note = applied.Succeeded ? modes.Note : await RestoreAfterFailureAsync(snapshot, null, cancellationToken);

        return await Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = modes.Plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Message = applied.Message,
            Note = note,
        }, started);
    }

    private async Task<SwitchResult> RollBackAsync(
        DisplaySnapshot before,
        AudioSwitcher.AudioRestore audioRestore,
        TopologyPlan plan,
        ApplyOutcome applied,
        AudioOutcome audio,
        ConfirmationResult answer,
        long started,
        CancellationToken cancellationToken)
    {
        Profile previous = PreviousTopology(before);
        ApplyOutcome rolledBack;
        try
        {
            DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
            TopologyPlan rollbackPlan = _planner.Plan(previous, now);
            LogPlan(rollbackPlan);

            rolledBack = rollbackPlan.Resolved.Count == 0
                ? new ApplyOutcome(false, rollbackPlan, 0, null, "None of the previously active displays is available.", false)
                : await ApplyWithRetryAsync(previous, rollbackPlan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken, restoring: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Restoring the previous topology threw");
            rolledBack = new ApplyOutcome(false, plan, 0, null, ex.Message, false);
        }
        finally
        {
            // Whatever the displays did: the sound must not stay on a headset that lies in the rig.
            await RestoreAudioAsync(audioRestore, cancellationToken);
        }

        if (!rolledBack.Succeeded)
        {
            string message = string.Create(CultureInfo.InvariantCulture,
                $"Switch was not confirmed ({answer}) and restoring the previous topology failed: {rolledBack.Message}");
            _log.Error("Rollback failed: {Reason}", rolledBack.Message);
            return await Finish(new SwitchResult
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

        _log.Information("Previous topology restored after {Attempts} attempts; switch rolled back after {Seconds:0.0} s",
            rolledBack.Attempts, _time.GetElapsedTime(started).TotalSeconds);
        return await Finish(new SwitchResult
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

    private async Task RestoreAudioAsync(AudioSwitcher.AudioRestore audioRestore, CancellationToken cancellationToken)
    {
        try
        {
            await _audioSwitcher.RestoreAsync(audioRestore, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "The previous audio devices could not be restored");
        }
    }

    /// <summary>
    /// A failed attempt may leave displays dark (Windows usually reverts on its own, but not reliably). If a display
    /// that was active before is no longer active, re-apply the previous topology.
    /// </summary>
    private async Task<SwitchNote> RestoreAfterFailureAsync(
        DisplaySnapshot before, SurroundSetting? surroundBefore, CancellationToken cancellationToken)
    {
        // Surround comes back first: while the wrong one runs, the displays of the old arrangement do not exist.
        await _surroundSwitcher.RestoreAsync(surroundBefore, cancellationToken);

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

        ApplyOutcome restored = await ApplyWithRetryAsync(previous, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken, restoring: true);
        if (!restored.Succeeded)
        {
            _log.Error("Restore after failed switch failed: {Reason}", restored.Message);
            return SwitchNote.RestoreFailed;
        }

        _log.Information("Previous topology restored after failed switch ({Attempts} attempts)", restored.Attempts);
        await RescueWindowsAsync(cancellationToken);
        return SwitchNote.RestoredPrevious;
    }

    /// <summary>What the Windows display layer throws when a query or an apply goes wrong (analysis finding B-07).</summary>
    private static bool IsDisplayApiFailure(Exception ex) =>
        ex is System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException;

    /// <summary>
    /// Stored modes first, then database modes (rule 5). On a transient error (31, 1610): wait, re-query, re-plan and try again
    /// within the time budget (rule 4). Every retry uses a fresh snapshot because LUIDs may change (rule 2).
    /// </summary>
    /// <param name="restoring">The previous arrangement comes back: displays Windows had duplicated are duplicated again.</param>
    private async Task<ApplyOutcome> ApplyWithRetryAsync(
        Profile profile, TopologyPlan plan, DateTimeOffset deadline, CancellationToken cancellationToken, bool restoring = false)
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
                    code = await _display.ApplyAsync(plan, new ApplyOptions { UseDatabaseModes = databaseModes, AllowClone = restoring }, cancellationToken)
                        .WaitAsync(_options.ApplyCallTimeout, _time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    // No second attempt: a call stuck in the driver holds whatever the next one would wait for.
                    _log.Error("Attempt {Attempt} did not return within {Timeout} ({ModeSource} modes); the graphics driver may hang",
                        attempts, _options.ApplyCallTimeout, modeSource);
                    return new ApplyOutcome(false, plan, attempts, lastError,
                        string.Create(CultureInfo.InvariantCulture,
                            $"The display driver did not return within {_options.ApplyCallTimeout.TotalSeconds} s."), false);
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
                plan = await PollTopologyAsync(profile, plan, deadline, force: true, includeDetached: true, cancellationToken);
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
    /// Re-queries the topology while a required display is attached but not ready, until the deadline. With
    /// <paramref name="includeDetached"/> a required display that is not connected is waited for too: a waking monitor can
    /// drop off the bus for seconds, and a switched-off one may be switched on (HW-16). <paramref name="force"/> re-queries
    /// at least once.
    /// </summary>
    private async Task<TopologyPlan> PollTopologyAsync(
        Profile profile, TopologyPlan plan, DateTimeOffset deadline, bool force, bool includeDetached, CancellationToken cancellationToken)
    {
        while ((force || IsWaitingForTarget(plan, includeDetached)) && _time.GetUtcNow() < deadline)
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
    /// state it had. Failures are logged and never fail the switch.
    /// </summary>
    private async Task SwitchHdrAsync(Profile profile, CancellationToken cancellationToken)
    {
        List<DisplayAssignment> wantedDisplays = [.. profile.Displays.Where(d => d.Hdr is not null)];
        if (wantedDisplays.Count == 0)
        {
            return;
        }

        long started = _time.GetTimestamp();
        try
        {
            DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
            if (!NeedsHdrCheck(wantedDisplays, now))
            {
                _log.Information("HDR for {Profile} is already as wanted", profile.Name);
                return;
            }

            // A monitor may still do its handshake right after the apply and drop off the bus; switching HDR then is what
            // froze the test PC (finding HW-12). Two snapshots in a row must agree first.
            if (await WaitForSettledDisplaysAsync(now, cancellationToken) is not { } settled)
            {
                _log.Warning("HDR for {Profile} left unchanged: the displays did not settle within {Budget}", profile.Name, _options.HdrSettleBudget);
                return;
            }

            HdrPass pass = await SetHdrAsync(wantedDisplays, settled, cancellationToken);
            if (!pass.TimedOut && pass.Unknown.Count > 0)
            {
                // Right after an apply the driver may not report HDR yet (analysis finding B-12): ask once more.
                _log.Information("HDR state of {Count} display(s) not reported yet, asking again in {Delay}", pass.Unknown.Count, HdrRetryDelay);
                await Task.Delay(HdrRetryDelay, _time, cancellationToken);
                now = await _display.QueryAsync(cancellationToken);
                foreach (DisplayAssignment wanted in (await SetHdrAsync(pass.Unknown, now, cancellationToken)).Unknown)
                {
                    _log.Warning("Display {Display} does not support HDR, left unchanged", DisplayNames.Of(wanted));
                }
            }

            _log.Information("HDR for {Profile} handled in {Milliseconds:0} ms", profile.Name, _time.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "HDR for {Profile} could not be set", profile.Name);
        }
    }

    private static readonly TimeSpan HdrRetryDelay = TimeSpan.FromSeconds(1);

    /// <summary>Whether an active display of the profile reports another HDR state than wanted, or none yet.</summary>
    private static bool NeedsHdrCheck(IReadOnlyList<DisplayAssignment> wantedDisplays, DisplaySnapshot now) =>
        wantedDisplays.Any(wanted => ActiveTarget(now, wanted)?.ActiveMode is { } mode && mode.Hdr != wanted.Hdr);

    private static AttachedDisplay? ActiveTarget(DisplaySnapshot snapshot, DisplayAssignment wanted) =>
        snapshot.Displays.FirstOrDefault(d => d.IsActive
            && string.Equals(d.Identity.TargetDevicePath, wanted.Identity.TargetDevicePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Re-queries until two snapshots in a row show the same displays with the same modes (HDR state aside), at most
    /// <see cref="SwitchOptions.HdrSettleBudget"/>. Returns the settled snapshot, or <c>null</c> when they kept changing.
    /// </summary>
    private async Task<DisplaySnapshot?> WaitForSettledDisplaysAsync(DisplaySnapshot first, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + _options.HdrSettleBudget;
        DisplaySnapshot previous = first;
        while (true)
        {
            await Task.Delay(_options.PollInterval, _time, cancellationToken);
            DisplaySnapshot current = await _display.QueryAsync(cancellationToken);
            if (LayoutKey(current).SetEquals(LayoutKey(previous)))
            {
                return current;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return null;
            }

            _log.Debug("Displays still changing after the apply, waiting before HDR");
            previous = current;
        }
    }

    private static HashSet<string> LayoutKey(DisplaySnapshot snapshot) =>
        snapshot.Displays
            .Select(d => d.ActiveMode is { } m
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{d.Identity.TargetDevicePath}|{d.IsAvailable}|{m.Width}x{m.Height}@{m.RefreshNumerator}/{m.RefreshDenominator}|{m.PositionX},{m.PositionY}")
                : string.Create(CultureInfo.InvariantCulture, $"{d.Identity.TargetDevicePath}|{d.IsAvailable}|off"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets HDR where the state differs. Returns the active displays that reported no HDR state, and whether a call did not
    /// return within <see cref="SwitchOptions.HdrCallTimeout"/> – then the remaining displays are left alone.
    /// </summary>
    private async Task<HdrPass> SetHdrAsync(IReadOnlyList<DisplayAssignment> wantedDisplays, DisplaySnapshot now, CancellationToken cancellationToken)
    {
        var unknown = new List<DisplayAssignment>();
        foreach (DisplayAssignment wanted in wantedDisplays)
        {
            AttachedDisplay? target = ActiveTarget(now, wanted);
            if (wanted.Hdr is not { } enabled || target?.ActiveMode is not { } mode)
            {
                continue;
            }

            if (mode.Hdr is null)
            {
                unknown.Add(wanted);
            }
            else if (mode.Hdr != enabled)
            {
                int code;
                try
                {
                    code = await _display.SetHdrAsync(target, enabled, cancellationToken).WaitAsync(_options.HdrCallTimeout, _time, cancellationToken);
                }
                catch (TimeoutException)
                {
                    _log.Error("HDR of {Display} did not return within {Timeout}; the graphics driver may hang. HDR is left alone for the rest of this switch",
                        DisplayNames.Of(wanted), _options.HdrCallTimeout);
                    return new HdrPass(unknown, TimedOut: true);
                }

                if (code != 0)
                {
                    _log.Warning("HDR of {Display} could not be set to {Enabled} (native error {Error})", DisplayNames.Of(wanted), enabled, code);
                }
            }
        }

        return new HdrPass(unknown, TimedOut: false);
    }

    private sealed record HdrPass(List<DisplayAssignment> Unknown, bool TimedOut);

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
            SwitchWithoutAsking = true,
        };
    }

    /// <summary>
    /// Keep-awake follows the profile. Failures are logged and never fail the switch.
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
    /// <summary>
    /// Puts the desktop symbols back where this profile wants them. Runs after the windows and under the same rule: only
    /// once the switch is staying, because a rejected switch must not leave the desktop rearranged.
    ///
    /// Explorer lays the symbols out itself when the arrangement changes, and it does so a moment after the change – so
    /// one attempt can be undone again right after it. Each pass therefore checks whether what it placed is still in
    /// place and repeats while something moved. Never throws: a desktop that cannot be tidied is a log line, not a
    /// failed switch.
    /// </summary>
    private async Task RestoreDesktopIconsAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.DesktopIcons is not { IsEmpty: false } wanted)
        {
            return;
        }

        try
        {
            for (int attempt = 1; attempt <= _options.DesktopIconAttempts; attempt++)
            {
                await Task.Delay(_options.DesktopIconDelay, _time, cancellationToken);
                DesktopIconResult result = _desktopIcons.Restore(wanted);
                if (result.Outcome != DesktopIconOutcome.Restored)
                {
                    _log.Information("Desktop symbols for {Profile}: {Outcome}", profile.Name, result.Outcome);
                    return;
                }

                _log.Information("Desktop symbols for {Profile}: {Placed} placed, {Missing} gone (attempt {Attempt})",
                    profile.Name, result.Placed, result.Missing, attempt);

                // What the shell actually made of it – with "align to grid" it snaps to the nearest cell, so comparing
                // against the wish would never agree. The next pass only has to notice that Explorer moved them again.
                DesktopIconLayout? settled = _desktopIcons.Capture();
                if (settled is null || attempt == _options.DesktopIconAttempts)
                {
                    return;
                }

                await Task.Delay(_options.DesktopIconDelay, _time, cancellationToken);
                if (!Moved(settled, _desktopIcons.Capture()))
                {
                    return;
                }

                _log.Information("Desktop symbols for {Profile} moved again, putting them back once more", profile.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the desktop symbols failed");
        }
    }

    /// <summary>Whether any symbol sits somewhere else than it did in <paramref name="settled"/>.</summary>
    private static bool Moved(DesktopIconLayout settled, DesktopIconLayout? now)
    {
        if (now is null)
        {
            return false;
        }

        Dictionary<string, DesktopIcon> current = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopIcon icon in now.Icons)
        {
            current[icon.Item] = icon;
        }

        return settled.Icons.Any(icon => current.TryGetValue(icon.Item, out DesktopIcon? at) && (at.X != icon.X || at.Y != icon.Y));
    }

    private async Task RescueWindowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.WindowRescueDelay, _time, cancellationToken);
            long started = _time.GetTimestamp();
            int moved = _windows.RescueOffscreenWindows();
            _log.Information("Moved {Count} window(s) from displays that are off to the primary display in {Milliseconds:0} ms",
                moved, _time.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Moving windows from displays that are off failed");
        }
    }

    /// <summary>
    /// At startup: a remembered value whose profile is no longer active (crash, restart or update in between) comes back
    /// now instead of waiting for the next switch. With a profile that disables ducking still active, it stays remembered.
    /// </summary>
    public Task RestoreDuckingIfUnusedAsync(Profile? activeProfile, CancellationToken cancellationToken) =>
        _duckingSwitcher.RestoreIfUnusedAsync(activeProfile, cancellationToken);

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

    /// <summary>
    /// Every way out of a switch passes here, so this is where the journal entry goes again – whether the switch was
    /// applied, blocked, failed or rolled back. Only an exception leaves it behind, and that is the case the next start
    /// should ask about. Clearing a record this switch never wrote is harmless: the app reads it once at startup,
    /// before any switch can run.
    /// </summary>
    private async Task<SwitchResult> Finish(SwitchResult result, long started)
    {
        await _journal.ClearAsync(CancellationToken.None);
        return result with { Duration = _time.GetElapsedTime(started) };
    }

    private sealed record ApplyOutcome(bool Succeeded, TopologyPlan Plan, int Attempts, int? LastNativeError, string? Message, bool UsedDatabaseModes);

    private sealed record ModeCheck(TopologyPlan Plan, SwitchNote Note, bool DisplaysDark)
    {
        public static ModeCheck AsPlanned(TopologyPlan plan) => new(plan, SwitchNote.None, DisplaysDark: false);
    }
}
