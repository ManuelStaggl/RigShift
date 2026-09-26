using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Runs one profile switch: Surround → plan → wait for sleeping targets → apply with retry and HDR → audio, keep-awake,
/// call ducking → confirm → rollback, or tidy-up and apps. Each step has its own class; this one decides their order and
/// the way back. The hard rules are in docs/display-topology.md.
/// </summary>
public sealed class SwitchOrchestrator
{
    private readonly HungDriverGuard _display;
    private readonly IPowerController _power;
    private readonly ISwitchConfirmation _confirmation;
    private readonly ISwitchJournal _journal;
    private readonly TopologyPlanner _planner;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly TopologyApplier _topology;
    private readonly PostSwitchTidy _tidy;
    private readonly AudioSwitcher _audioSwitcher;
    private readonly AppRunner _appRunner;
    private readonly DuckingSwitcher _duckingSwitcher;
    private readonly SurroundSwitcher _surroundSwitcher;
    private readonly AllDisplaysOn _allDisplaysOn;

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

        // The app hands in the guard all its pages share; anything else (tests, the command line) gets one of its own.
        _display = display as HungDriverGuard ?? new HungDriverGuard(display, options, time, log);
        _power = power;
        _confirmation = confirmation;
        _journal = journal;
        _planner = planner;
        _options = options;
        _time = time;
        _log = log.ForContext<SwitchOrchestrator>();
        _topology = new TopologyApplier(_display, planner, options, time, _log);
        _tidy = new PostSwitchTidy(windows, desktopIcons, options, time, _log);
        _audioSwitcher = new AudioSwitcher(audio, options, time, _log);
        _appRunner = new AppRunner(apps, usbDevices, options, time, _log);
        _duckingSwitcher = new DuckingSwitcher(ducking, duckingMemory, _log);
        _surroundSwitcher = new SurroundSwitcher(surround, _log);
        _allDisplaysOn = new AllDisplaysOn(_display, _log);
    }

    /// <summary>
    /// Raised on the switch's thread when required displays are not connected or not ready and the switch waits for the
    /// user to switch them on (findings HW-16, K-06). Carries those displays.
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

        // A new switch ends what the previous one left running: apps, e.g. still waiting for their device (analysis finding
        // B-03), and its tidy-up (K-04).
        _ = CancelPendingAsync();

        var wayBack = new WayBack(await _display.QueryAsync(cancellationToken), await _surroundSwitcher.CaptureAsync(profile, cancellationToken));
        DisplaySnapshot planFrom = wayBack.Displays;

        // Before anything is recorded, so the record holds the sound to go back to as well (K-14).
        TimeSpan? confirmTimeout = ConfirmTimeout(profile, request);
        if (confirmTimeout is not null)
        {
            await CaptureRestAsync(profile, wayBack, cancellationToken);
        }

        // Surround first: switching it on or off turns several monitors into one wide one and back, so it decides which
        // displays the arrangement can address at all - planning before it would plan against displays about to vanish.
        SurroundApplyResult surround = SurroundApplyResult.NotConfigured;
        if (profile.Surround is not null)
        {
            await RecordAsync(profile, wayBack, cancellationToken);
            surround = await _surroundSwitcher.SwitchAsync(profile, cancellationToken);
            if (surround.Outcome == SurroundOutcome.Failed)
            {
                // Nothing else was touched yet, so this is a block, not a half-finished switch. Surround itself may have
                // changed halfway, though - the displays were woken, or the driver answered "failed" after acting - so it
                // goes back to what it was; a state that did not change costs one read.
                await _surroundSwitcher.RestoreAsync(wayBack.Surround, cancellationToken);
                return await Finish(
                    new SwitchResult
                    {
                        Outcome = SwitchOutcome.Blocked,
                        Plan = _planner.Plan(profile, wayBack.Displays),
                        Surround = surround.Outcome,
                        Message = surround.Message,
                    },
                    started);
            }

            if (surround.Outcome == SurroundOutcome.Changed)
            {
                // Plan against what exists now. The way back stays the state before Surround.
                planFrom = await SettleAfterSurroundAsync(profile, cancellationToken);
            }
        }

        TopologyPlan plan = await PlanAndWaitAsync(profile, planFrom, cancellationToken);
        if (TopologyApplier.BlockReason(plan) is { } blocked)
        {
            _log.Warning("Switch to {Profile} blocked: {Reason}", profile.Name, blocked);
            await _surroundSwitcher.RestoreAsync(wayBack.Surround, cancellationToken);
            return await Finish(
                new SwitchResult { Outcome = SwitchOutcome.Blocked, Plan = plan, Surround = surround.Outcome, Message = blocked },
                started);
        }

        // Waiting for a sleeping display may have used up most of the budget; a display that wakes late and then answers
        // 31 still needs time for its retries (analysis finding B-09).
        DateTimeOffset deadline = _time.GetUtcNow() + _options.TargetWaitBudget;

        await RecordAsync(profile, wayBack, cancellationToken);
        ApplyOutcome applied = await _topology.ApplyAsync(profile, plan, deadline, cancellationToken);
        if (!applied.Succeeded)
        {
            _log.Error("Switch to {Profile} failed after {Attempts} attempts: {Reason}", profile.Name, applied.Attempts, applied.Message);
            SwitchNote restored = await RestoreAfterFailureAsync(wayBack.Displays, wayBack.Surround, cancellationToken);
            SwitchResult failed = await Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = applied.Plan,
                Attempts = applied.Attempts,
                LastNativeError = applied.LastNativeError,
                Message = applied.Message,
                Surround = surround.Outcome,
                Note = restored,
            }, started);
            return restored == SwitchNote.RestoredPrevious ? failed with { TidyCompletion = _tidy.Start(null) } : failed;
        }

        Answer answer = await ApplyRestAndAskAsync(profile, wayBack, applied, confirmTimeout, started, cancellationToken);
        if (answer.Result != ConfirmationResult.Confirmed)
        {
            return await RollBackAsync(profile, wayBack, applied, answer, started, cancellationToken);
        }

        // A missing optional display (spacedesk viewer) is no partial switch; the catch-up follows it (finding HW-03).
        SwitchOutcome outcome = answer.Modes.DisplaysDark ? SwitchOutcome.AppliedPartially : SwitchOutcome.Applied;
        AppsOutcome apps = PendingApps(profile);
        _log.Information("Switch to {Profile} finished: {Outcome}, audio {Audio}, apps {Apps}, {Attempts} attempts, {Seconds:0.0} s",
            profile.Name, outcome, answer.Audio, apps, applied.Attempts, _time.GetElapsedTime(started).TotalSeconds);
        SwitchResult result = await Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = answer.Modes.Plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Audio = answer.Audio,
            Apps = apps,
            Surround = surround.Outcome,
            Note = answer.Modes.Note,
            Hdr = applied.Hdr,
            DesktopIcons = PendingDesktopIcons(profile),
        }, started);
        return AfterResult(result, profile);
    }

    /// <summary>
    /// What follows a switch that stays, started once its result is there (v4 finding K-04). Only then: a rejected switch
    /// must not leave windows moved without a way back (B-14), programs started or someone's work closed. In the
    /// background: neither Windows' own rearranging nor an app waiting for its device may hold up the result, the hotkeys,
    /// the automation or the next switch (B-03). The journal is gone by then, so an exit meanwhile brings no question
    /// "undo it?" at the next start (K-14).
    /// </summary>
    private SwitchResult AfterResult(SwitchResult result, Profile profile) =>
        result with { TidyCompletion = _tidy.Start(profile), AppsCompletion = _appRunner.Start(profile) };

    private static AppsOutcome PendingApps(Profile profile) =>
        profile.Apps.Count == 0 ? AppsOutcome.NotConfigured : AppsOutcome.Pending;

    private static DesktopIconOutcome PendingDesktopIcons(Profile profile) =>
        profile.DesktopIcons is { IsEmpty: false } ? DesktopIconOutcome.Pending : DesktopIconOutcome.NotConfigured;

    /// <summary>
    /// The displays after a Surround change. Windows lists the displays of a grid that was just built or taken apart
    /// seconds later, not at once; until the profile's required displays are there, this looks again quietly. Planning
    /// at once used to ask the user to switch on monitors that were on (finding K-09).
    /// </summary>
    private async Task<DisplaySnapshot> SettleAfterSurroundAsync(Profile profile, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        DateTimeOffset deadline = _time.GetUtcNow() + _options.SurroundSettleBudget;
        while (true)
        {
            DisplaySnapshot snapshot = await _display.QueryAsync(cancellationToken);
            bool complete = !_planner.Plan(profile, snapshot).Missing
                .Any(m => !m.Assignment.IsOptional && m.Reason is MissingReason.NotAttached or MissingReason.AwaitsSurround);
            if (complete || _time.GetUtcNow() >= deadline)
            {
                _log.Information("Displays after the Surround change {State} after {Milliseconds:0} ms",
                    complete ? "complete" : "still incomplete", _time.GetElapsedTime(started).TotalMilliseconds);
                return snapshot;
            }

            await Task.Delay(_options.SurroundPollInterval, _time, cancellationToken);
        }
    }

    /// <summary>
    /// Plans the profile and waits up to <see cref="SwitchOptions.MissingDisplayWaitBudget"/> for its required displays
    /// that are not connected or not ready, after asking the user to switch them on.
    /// </summary>
    private async Task<TopologyPlan> PlanAndWaitAsync(Profile profile, DisplaySnapshot snapshot, CancellationToken cancellationToken)
    {
        TopologyPlan plan = _planner.Plan(profile, snapshot);
        _topology.LogPlan(plan);
        if (plan.IsAmbiguous)
        {
            // Identical monitors on new ports: switching one on would change nothing, so neither ask for it nor wait (K-03).
            return plan;
        }

        List<DisplayAssignment> notThere = [.. plan.Missing
            .Where(m => !m.Assignment.IsOptional && m.Reason is MissingReason.NotAttached or MissingReason.AttachedButUnavailable)
            .Select(m => m.Assignment)];
        if (notThere.Count == 0)
        {
            return plan;
        }

        // A monitor that left the bus cannot be woken by software (no CEC on GPUs, DDC/CI needs the link): ask the user to
        // switch it on and wait for it instead of blocking at once (finding HW-16). The same for one Windows still lists but
        // that does not answer - that used to be 20 silent seconds and then a block (K-06).
        _log.Information("Waiting up to {Seconds:0} s for required displays that are not connected or not ready: {Displays}",
            _options.MissingDisplayWaitBudget.TotalSeconds, string.Join(", ", notThere.Select(d => DisplayNames.Of(d))));
        WaitingForDisplays?.Invoke(this, notThere);
        return await _topology.WaitForDisplaysAsync(
            profile, plan, _time.GetUtcNow() + _options.MissingDisplayWaitBudget, force: false, includeDetached: true, cancellationToken);
    }

    /// <summary>How long the switch waits for "keep", or <c>null</c> when it does not ask.</summary>
    private static TimeSpan? ConfirmTimeout(Profile profile, SwitchRequest request)
    {
        TimeSpan? timeout;

        // A link may come from a web page: it always asks, at least with the default timeout (analysis finding H-02).
        if (request.FromLink)
        {
            timeout = request.DefaultConfirmTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(request.DefaultConfirmTimeoutSeconds)
                : SwitchOptions.DefaultConfirmTimeout;
        }
        else
        {
            timeout = request.DefaultConfirmTimeoutSeconds > 0 && !profile.SwitchWithoutAsking && !request.SkipConfirmation
                ? TimeSpan.FromSeconds(request.DefaultConfirmTimeoutSeconds)
                : null;
        }

        TimeSpan minimum = TimeSpan.FromSeconds(request.MinimumConfirmTimeoutSeconds);
        return timeout < minimum ? minimum : timeout;
    }

    /// <summary>Audio, keep-awake and call ducking as they are now: only a switch that asks can be rejected and undo them.</summary>
    private async Task CaptureRestAsync(Profile profile, WayBack wayBack, CancellationToken cancellationToken)
    {
        wayBack.Audio = await _audioSwitcher.CaptureAsync(profile.Audio, cancellationToken);
        wayBack.KeepAwake = _power.IsKeepingAwake;
        wayBack.Ducking = await _duckingSwitcher.CaptureAsync(profile, cancellationToken);
    }

    /// <summary>
    /// Records the way back before the first change, not after: the crash this guards against can happen in between. A
    /// switch that never gets that far leaves no record at all.
    /// </summary>
    private async Task RecordAsync(Profile profile, WayBack wayBack, CancellationToken cancellationToken)
    {
        if (wayBack.Recorded)
        {
            return;
        }

        wayBack.Recorded = true;
        await _journal.BeginAsync(
            new InterruptedSwitch
            {
                Previous = TopologyApplier.PreviousTopology(wayBack.Displays) with { Surround = wayBack.Surround, Audio = wayBack.Audio.AsAssignment() },
                TargetProfileName = profile.Name,
                StartedUtc = _time.GetUtcNow(),
            },
            cancellationToken);
    }

    /// <summary>
    /// The rest of the profile – audio, keep-awake, call ducking – and then the question whether to keep it. From the apply
    /// until the answer the screens show an arrangement nobody confirmed, possibly on displays the user cannot see.
    /// Whatever is thrown in between – a dialog that cannot be shown, a COM surprise, the app exiting – ends in the way
    /// back, never in that arrangement staying.
    /// </summary>
    private async Task<Answer> ApplyRestAndAskAsync(
        Profile profile, WayBack wayBack, ApplyOutcome applied, TimeSpan? confirmTimeout, long started, CancellationToken cancellationToken)
    {
        bool confirm = confirmTimeout is not null;
        ModeCheck modes = ModeCheck.AsPlanned(applied.Plan);
        AudioOutcome audio = AudioOutcome.NotConfigured;
        try
        {
            if (applied.UsedDatabaseModes)
            {
                modes = await _topology.CheckDatabaseModesAsync(applied.Plan, cancellationToken);
            }

            long audioStarted = _time.GetTimestamp();
            // A display that was off may carry the profile's sound device, which wakes a moment after its picture (K-02).
            bool displaysTurnedOn = applied.Plan.Resolved.Any(d => !d.Target.IsActive);
            audio = await _audioSwitcher.SwitchAsync(profile.Audio, displaysTurnedOn, cancellationToken);
            if (audio != AudioOutcome.NotConfigured)
            {
                _log.Information("Audio for {Profile}: {Audio} after {Milliseconds:0} ms", profile.Name, audio, _time.GetElapsedTime(audioStarted).TotalMilliseconds);
            }

            // With audio, not with apps: both are undone without loss, and the countdown should already run kept awake.
            SwitchKeepAwake(profile);
            await _duckingSwitcher.SwitchAsync(profile, cancellationToken);

            // What the user waits for, from the click until picture and sound are there (v4 finding K-04).
            _log.Information("Picture and sound of {Profile} ready after {Milliseconds:0} ms", profile.Name, _time.GetElapsedTime(started).TotalMilliseconds);

            if (confirmTimeout is not { } timeout)
            {
                return new Answer(modes, audio, ConfirmationResult.Confirmed, Thrown: null);
            }

            long askedAt = _time.GetTimestamp();
            ConfirmationResult result = await _confirmation.ConfirmAsync(profile, wayBack.Displays, timeout, cancellationToken);
            if (result == ConfirmationResult.Confirmed)
            {
                _log.Information("Switch to {Profile} confirmed after {Seconds:0.0} s", profile.Name, _time.GetElapsedTime(askedAt).TotalSeconds);
            }

            return new Answer(modes, audio, result, Thrown: null);
        }
        catch (OperationCanceledException) when (confirm && cancellationToken.IsCancellationRequested)
        {
            return new Answer(modes, audio, ConfirmationResult.Cancelled, Thrown: null);
        }
        catch (Exception ex) when (confirm)
        {
            _log.Error(ex, "Switch to {Profile} threw before it was confirmed, rolling back", profile.Name);
            return new Answer(modes, audio, ConfirmationResult.Cancelled, ex);
        }
    }

    /// <summary>
    /// Windows restored the profile's display layout on its own, e.g. when a monitor was switched on (finding HW-15): the
    /// rest of the profile follows without a display change and without asking – the user chose it from the notification.
    /// </summary>
    private async Task<SwitchResult> ApplyRestAsync(Profile profile, CancellationToken cancellationToken)
    {
        long started = _time.GetTimestamp();
        _log.Information("Applying the rest of profile {Profile}; Windows already restored its displays", profile.Name);
        _ = CancelPendingAsync();

        TopologyPlan plan = _planner.Plan(profile, await _display.QueryAsync(cancellationToken));
        AudioOutcome audio = await _audioSwitcher.SwitchAsync(profile.Audio, displaysTurnedOn: true, cancellationToken);
        SwitchKeepAwake(profile);
        await _duckingSwitcher.SwitchAsync(profile, cancellationToken);

        AppsOutcome apps = PendingApps(profile);
        _log.Information("Rest of {Profile} applied: audio {Audio}, apps {Apps}", profile.Name, audio, apps);
        SwitchResult result = await Finish(
            new SwitchResult { Outcome = SwitchOutcome.Applied, Plan = plan, Audio = audio, Apps = apps, DesktopIcons = PendingDesktopIcons(profile) },
            started);
        return AfterResult(result, profile);
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
        _topology.LogPlan(plan);
        _log.Information("Dry run of {Profile} finished in {Milliseconds:0} ms", profile.Name, _time.GetElapsedTime(started).TotalMilliseconds);

        // Not through Finish: a switch waiting for "keep" next to this dry run still needs its record (K-05).
        return new SwitchResult { Outcome = SwitchOutcome.DryRun, Plan = plan, Duration = _time.GetElapsedTime(started) };
    }

    /// <summary>
    /// Cancels what an earlier switch left running after its result – apps that still start or wait for their device, and
    /// the tidy-up – and completes once it ended. The cancellation is requested before this method first yields.
    /// </summary>
    public Task CancelPendingAsync() => Task.WhenAll(_appRunner.CancelPendingAsync(), _tidy.CancelPendingAsync());

    /// <summary>
    /// The emergency hotkey: every display that is ready, at 60 Hz when the card cannot drive them all otherwise. No
    /// profile, no confirmation – whoever presses it sees no picture. The caller makes sure no switch runs meanwhile.
    /// </summary>
    public Task<AllDisplaysOnResult> TurnAllDisplaysOnAsync(CancellationToken cancellationToken) => _allDisplaysOn.RunAsync(cancellationToken);

    /// <summary>
    /// After a partial switch, a skipped optional display (spacedesk viewer) may appear later. Re-plans the profile and
    /// re-applies the full path set when more displays resolve than were applied. No confirmation and no audio – the user
    /// already accepted this profile. Returns null when nothing changed.
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
        _topology.LogPlan(plan);

        ApplyOutcome applied = await _topology.ApplyAsync(profile, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken);
        ModeCheck modes = applied.Succeeded && applied.UsedDatabaseModes
            ? await _topology.CheckDatabaseModesAsync(applied.Plan, cancellationToken)
            : ModeCheck.AsPlanned(applied.Plan);
        SwitchOutcome outcome = !applied.Succeeded ? SwitchOutcome.Failed
            : modes.DisplaysDark ? SwitchOutcome.AppliedPartially
            : SwitchOutcome.Applied;
        _log.Information("Catch-up of {Profile} finished: {Outcome}, {Attempts} attempts, {Seconds:0.0} s",
            profile.Name, outcome, applied.Attempts, _time.GetElapsedTime(started).TotalSeconds);

        // A failed catch-up can leave displays dark just like a failed switch (analysis finding B-06).
        SwitchNote note = applied.Succeeded ? modes.Note : await RestoreAfterFailureAsync(snapshot, null, cancellationToken);

        // Tidied up like a switch, after the result (K-04): the arrangement changed, so Explorer lays out the symbols anew.
        bool tidy = applied.Succeeded || note == SwitchNote.RestoredPrevious;
        SwitchResult result = await Finish(new SwitchResult
        {
            Outcome = outcome,
            Plan = modes.Plan,
            Attempts = applied.Attempts,
            LastNativeError = applied.LastNativeError,
            Message = applied.Message,
            Note = note,
            DesktopIcons = tidy ? PendingDesktopIcons(profile) : DesktopIconOutcome.NotConfigured,
        }, started);
        return tidy ? result with { TidyCompletion = _tidy.Start(profile) } : result;
    }

    /// <summary>
    /// The switch was not confirmed: rejected, timed out, cancelled, or something threw before the answer. Everything it
    /// changed goes back.
    /// </summary>
    private async Task<SwitchResult> RollBackAsync(
        Profile profile, WayBack wayBack, ApplyOutcome applied, Answer answer, long started, CancellationToken cancellationToken)
    {
        // Cancelled (app exit, logoff): the window closing is no answer, and the rollback must run to the end before the
        // caller learns about the cancellation (analysis finding B-02).
        bool cancelled = answer.Thrown is null && cancellationToken.IsCancellationRequested;
        CancellationToken rollbackToken = cancelled || answer.Thrown is not null ? CancellationToken.None : cancellationToken;
        if (cancelled)
        {
            _log.Warning("Switch to {Profile} cancelled during confirmation, rolling back", profile.Name);
        }
        else if (answer.Thrown is null)
        {
            _log.Warning("Switch to {Profile} not confirmed ({Answer}), rolling back", profile.Name, answer.Result);
        }

        RestoreKeepAwake(wayBack.KeepAwake);
        string? surroundFailure = null;
        try
        {
            await _duckingSwitcher.RestoreAsync(wayBack.Ducking, rollbackToken);

            // Surround comes back first: while the wrong one runs, the displays of the old arrangement do not exist.
            surroundFailure = await _surroundSwitcher.RestoreAsync(wayBack.Surround, rollbackToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The displays matter most: nothing on the way there may keep the rollback from reaching them.
            _log.Error(ex, "Restoring ducking or Surround threw, the displays are rolled back regardless");
        }

        SwitchResult rolledBack = await RollBackDisplaysAsync(wayBack, applied, answer, surroundFailure, started, rollbackToken);
        if (cancelled)
        {
            _log.Warning("Switch to {Profile} cancelled, rolled back ({Outcome})", profile.Name, rolledBack.Outcome);
            throw new OperationCanceledException(cancellationToken);
        }

        if (answer.Thrown is { } thrown)
        {
            return rolledBack with
            {
                Outcome = SwitchOutcome.Failed,
                Message = string.Create(CultureInfo.InvariantCulture, $"{thrown.Message} {rolledBack.Message}"),
            };
        }

        return rolledBack;
    }

    /// <summary>The displays and the sound of a rollback, and the result it ends with.</summary>
    private async Task<SwitchResult> RollBackDisplaysAsync(
        WayBack wayBack, ApplyOutcome applied, Answer answer, string? surroundFailure, long started, CancellationToken cancellationToken)
    {
        ApplyOutcome rolledBack;
        try
        {
            DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
            rolledBack = await _topology.RestoreAsync(wayBack.Displays, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Restoring the previous topology threw");
            rolledBack = ApplyOutcome.Failure(answer.Modes.Plan, 0, null, ex.Message);
        }
        finally
        {
            // Whatever the displays did: the sound must not stay on a headset that lies in the rig.
            await RestoreAudioAsync(wayBack.Audio, cancellationToken);
        }

        if (!rolledBack.Succeeded)
        {
            string message = string.Create(CultureInfo.InvariantCulture,
                $"Switch was not confirmed ({answer.Result}) and restoring the previous topology failed: {rolledBack.Message}");
            _log.Error("Rollback failed: {Reason}", rolledBack.Message);
            return await Finish(new SwitchResult
            {
                Outcome = SwitchOutcome.Failed,
                Plan = answer.Modes.Plan,
                Attempts = applied.Attempts + rolledBack.Attempts,
                LastNativeError = rolledBack.LastNativeError,
                Message = message,
                Note = _display.IsHung ? SwitchNote.DriverHung : SwitchNote.RestoreFailed,
                Audio = answer.Audio,
            }, started);
        }

        _log.Information("Previous topology restored after {Attempts} attempts; switch rolled back after {Seconds:0.0} s",
            rolledBack.Attempts, _time.GetElapsedTime(started).TotalSeconds);
        return await Finish(new SwitchResult
        {
            Outcome = SwitchOutcome.RolledBack,
            Plan = answer.Modes.Plan,
            Attempts = applied.Attempts + rolledBack.Attempts,
            LastNativeError = applied.LastNativeError,
            Message = surroundFailure is null
                ? string.Create(CultureInfo.InvariantCulture, $"Not confirmed ({answer.Result}); previous topology restored.")
                : string.Create(CultureInfo.InvariantCulture,
                    $"Not confirmed ({answer.Result}); displays restored, but Surround could not be put back: {surroundFailure}"),
            Note = surroundFailure is null ? SwitchNote.RestoredPrevious : SwitchNote.SurroundNotRestored,
            Audio = answer.Audio,
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
        if (_display.IsHung)
        {
            // Every display call fails at once until the stuck one returns; only a restart helps (K-07).
            _log.Error("Nothing restored after the failed switch: the graphics driver does not answer");
            return SwitchNote.DriverHung;
        }

        // Surround comes back first: while the wrong one runs, the displays of the old arrangement do not exist.
        await _surroundSwitcher.RestoreAsync(surroundBefore, cancellationToken);

        DisplaySnapshot now;
        try
        {
            now = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (DisplayApiFailure.Is(ex))
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
        ApplyOutcome restored = await _topology.RestoreAsync(before, now, cancellationToken);
        if (!restored.Succeeded)
        {
            _log.Error("Restore after failed switch failed: {Reason}", restored.Message);
            return SwitchNote.RestoreFailed;
        }

        // The caller moves lost windows over once its result is out.
        _log.Information("Previous topology restored after failed switch ({Attempts} attempts)", restored.Attempts);
        return SwitchNote.RestoredPrevious;
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
    /// At startup: a remembered value whose profile is no longer active (crash, restart or update in between) comes back
    /// now instead of waiting for the next switch. With a profile that disables ducking still active, it stays remembered.
    /// </summary>
    public Task RestoreDuckingIfUnusedAsync(Profile? activeProfile, CancellationToken cancellationToken) =>
        _duckingSwitcher.RestoreIfUnusedAsync(activeProfile, cancellationToken);

    /// <summary>
    /// Every way out of a switch passes here, so this is where the journal entry goes again – whether the switch was
    /// applied, blocked, failed or rolled back. Only an exception leaves it behind, and that is the case the next start
    /// should ask about. Clearing a record this switch never wrote is harmless: the app reads it once at startup,
    /// before any switch can run, and only one switch runs at a time. A dry run runs next to a switch (B-13), so it never
    /// comes here (K-05).
    /// </summary>
    private async Task<SwitchResult> Finish(SwitchResult result, long started)
    {
        await _journal.ClearAsync(CancellationToken.None);
        return result with { Duration = _time.GetElapsedTime(started) };
    }

    /// <summary>
    /// What a switch needs to go back, taken before it changes anything: the displays and Surround always, the rest of the
    /// profile only when the switch asks – only then can it be rejected.
    /// </summary>
    private sealed class WayBack(DisplaySnapshot displays, SurroundSetting? surround)
    {
        public DisplaySnapshot Displays { get; } = displays;

        public SurroundSetting? Surround { get; } = surround;

        public AudioSwitcher.AudioRestore Audio { get; set; } = AudioSwitcher.AudioRestore.Nothing;

        public bool? KeepAwake { get; set; }

        public DuckingSwitcher.DuckingRestore? Ducking { get; set; }

        /// <summary>Whether the journal holds this way back.</summary>
        public bool Recorded { get; set; }
    }

    /// <summary>How far a switch got until its answer, and the answer.</summary>
    /// <param name="Modes">The modes that run and the plan as it came out.</param>
    /// <param name="Thrown">Something failed before the answer; the switch goes back and reports it as failed.</param>
    private sealed record Answer(ModeCheck Modes, AudioOutcome Audio, ConfirmationResult Result, Exception? Thrown);
}
