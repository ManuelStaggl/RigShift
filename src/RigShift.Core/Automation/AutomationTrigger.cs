namespace RigShift.Core.Automation;

public enum TriggerReason
{
    /// <summary>The device connected.</summary>
    Started,

    /// <summary>The device is gone.</summary>
    Ended,
}

/// <summary>A switch the automation asks for.</summary>
public sealed record TriggerAction(AutomationRule Rule, Guid ProfileId, TriggerReason Reason)
{
    public bool SkipConfirmation => Rule.SkipConfirmation;
}

public enum TriggerEventKind
{
    /// <summary>First poll after start, reset or a rule change: the device state is taken as it is.</summary>
    Baseline,

    DeviceConnected,

    /// <summary>The device is gone; the end action waits for <see cref="TriggerEvent.Delay"/>.</summary>
    DeviceGone,

    /// <summary>The device came back before its end action ran.</summary>
    DeviceBack,

    /// <summary>
    /// The rule started with the end action "switch back", but no other profile was active, so there is nothing to go back
    /// to (analysis finding C-05).
    /// </summary>
    NoPreviousProfile,

    /// <summary>The device stayed gone long enough, but the end action does nothing (<see cref="TriggerEvent.SkipReason"/>).</summary>
    ExitSkipped,

    /// <summary>The start switch did not succeed; the rule waits as told by <see cref="TriggerEvent.Retry"/>.</summary>
    Disarmed,

    /// <summary>
    /// The device stayed gone long enough, but a full-screen application is running: the end action waits until it
    /// closes, so a USB hiccup during a race never switches the displays away (1.7.0). Reported once per wait.
    /// </summary>
    ExitHeld,
}

public enum ExitSkipReason
{
    None,

    /// <summary>The rule did not start the current session (device present at baseline, or the start failed).</summary>
    NotStartedByRule,

    /// <summary>A profile other than the rule's is active – the user picked it.</summary>
    OtherProfileActive,

    /// <summary>End action "stay".</summary>
    Stay,

    /// <summary>"Switch back", but no profile was active when the device connected.</summary>
    NoPreviousProfile,

    /// <summary>The target profile is active already.</summary>
    TargetActive,
}

/// <summary>How a rule behaves after its start switch did not succeed.</summary>
public enum RetryMode
{
    /// <summary>Another switch was running or the displays were not ready: try again once the delay has passed.</summary>
    Later,

    /// <summary>The user rejected the switch or it failed: only a reconnect starts it again, never a loop.</summary>
    AfterReconnect,
}

/// <summary>A decision of the trigger, for the log (analysis finding K-02).</summary>
public sealed record TriggerEvent(AutomationRule Rule, TriggerEventKind Kind)
{
    public TimeSpan? Delay { get; init; }

    public bool? DevicePresent { get; init; }

    public ExitSkipReason SkipReason { get; init; }

    public RetryMode? Retry { get; init; }
}

public sealed record TriggerEvaluation(IReadOnlyList<TriggerAction> Actions, IReadOnlyList<TriggerEvent> Events);

/// <summary>
/// Decides from polled USB devices when rules switch (docs/PLAN.md, section 6). Pure logic: the caller supplies what is
/// present, the active profile and a monotonic time.
/// </summary>
/// <remarks>
/// Start: a device that appears switches to the rule's profile, unless it is active already. Whatever is present at the
/// first poll only sets the baseline, so starting RigShift with the device already connected changes nothing.
/// End: acted on once the device has been gone for the rule's <see cref="ExitDelayOf"/> and for at least two polls in a
/// row (a single missed poll is no end, even with a delay of 0), and only while the rule's profile is still active – a
/// profile the user picked in the meantime is not overridden. A start switch that did not succeed is reported back with
/// <see cref="Disarm"/>, so the rule does not count as started (analysis finding C-01).
/// </remarks>
public sealed class AutomationTrigger
{
    /// <summary>The shortest wait before a rule retries a start that could not run.</summary>
    public static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(10);

    private const int PollsGoneForExit = 2;

    private readonly Dictionary<Guid, RuleState> _states = [];
    private bool _hasBaseline;

    /// <summary>The rule's <see cref="AutomationRule.ExitDelaySeconds"/>, kept between 0 and 10 minutes.</summary>
    public static TimeSpan ExitDelayOf(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return TimeSpan.FromSeconds(Math.Clamp(rule.ExitDelaySeconds, 0, AutomationRule.MaxExitDelaySeconds));
    }

    /// <summary>Forget everything, e.g. after pausing or waking from sleep: the next poll sets a new baseline.</summary>
    public void Reset()
    {
        _states.Clear();
        _hasBaseline = false;
    }

    /// <summary>
    /// The devices a rule watches as <c>VID_xxxx&amp;PID_xxxx</c>, sorted and without repeats; empty for a rule without a
    /// valid device id.
    /// </summary>
    public static IReadOnlyList<string> WatchedDevicesOf(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return (rule.Migrated().Devices ?? [])
            .Select(d => UsbDeviceIds.Normalize(d.Id))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>A rule with neither devices nor the 1.3 device key was written by an unreleased build and is ignored.</summary>
    public static bool IsIgnored(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return rule.Devices is null && rule.LegacyUsbDeviceId is null;
    }

    /// <summary>
    /// The start switch of <paramref name="rule"/> did not succeed (busy, blocked, failed or rejected): the rule no longer
    /// counts as started, so neither a later end action nor a missed start depends on it.
    /// </summary>
    public TriggerEvent? Disarm(AutomationRule rule, RetryMode retry, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!_states.TryGetValue(rule.Id, out RuleState? state))
        {
            return null;
        }

        state.StartedByRule = false;
        state.PreviousProfileId = null;
        state.GoneSince = null;
        state.PollsGone = 0;
        state.ExitHeld = false;
        if (retry == RetryMode.Later)
        {
            // Not running: the next poll with the device present is a start again, once the wait is over.
            TimeSpan delay = ExitDelayOf(rule) > MinimumRetryDelay ? ExitDelayOf(rule) : MinimumRetryDelay;
            state.IsRunning = false;
            state.RetryAt = now + delay;
            return new TriggerEvent(rule, TriggerEventKind.Disarmed) { Retry = retry, Delay = delay };
        }

        // Running stays as it is: only disconnecting and connecting the device starts again.
        state.RetryAt = null;
        return new TriggerEvent(rule, TriggerEventKind.Disarmed) { Retry = retry };
    }

    /// <param name="present">Connected devices as <c>VID_xxxx&amp;PID_xxxx</c>, compared without case.</param>
    /// <param name="now">Monotonic time, e.g. <see cref="TimeProvider.GetElapsedTime(long)"/> since the service started.</param>
    /// <param name="exitBlocked">
    /// Asked at most once per poll, and only when an end action is due: <c>true</c> holds it back (a full-screen game is
    /// running). The device coming back meanwhile cancels the end action as usual.
    /// </param>
    public TriggerEvaluation Evaluate(
        IReadOnlyList<AutomationRule> rules, IReadOnlySet<string> present, Guid? activeProfileId, TimeSpan now, Func<bool>? exitBlocked = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(present);

        foreach (Guid removed in _states.Keys.Where(id => rules.All(r => r.Id != id)).ToList())
        {
            _states.Remove(removed);
        }

        var actions = new List<TriggerAction>();
        var events = new List<TriggerEvent>();
        bool? blocked = null;
        foreach (AutomationRule rule in rules)
        {
            if (IsIgnored(rule))
            {
                continue;
            }

            // A combination runs while all of its devices are present (user decision U-02).
            IReadOnlyList<string> devices = WatchedDevicesOf(rule);
            string watched = string.Join('+', devices);
            bool running = devices.Count > 0 && devices.All(present.Contains);
            if (!_states.TryGetValue(rule.Id, out RuleState? state) || state.Watched != watched)
            {
                // A new rule, or one whose devices were changed while they are present, behaves like the baseline: no
                // switch until the next start.
                _states[rule.Id] = new RuleState { IsRunning = running, Watched = watched };
                events.Add(new TriggerEvent(rule, TriggerEventKind.Baseline) { DevicePresent = running });
                continue;
            }

            if (!_hasBaseline)
            {
                state.IsRunning = running;
                events.Add(new TriggerEvent(rule, TriggerEventKind.Baseline) { DevicePresent = running });
                continue;
            }

            if (running)
            {
                bool restarted = state.GoneSince is not null;
                state.GoneSince = null;
                state.PollsGone = 0;
                state.ExitHeld = false;
                if (restarted)
                {
                    events.Add(new TriggerEvent(rule, TriggerEventKind.DeviceBack));
                }

                if (!state.IsRunning && !(state.RetryAt is { } retryAt && now < retryAt))
                {
                    state.IsRunning = true;
                    state.RetryAt = null;

                    // Back within the delay continues the session the rule started; otherwise it is a new start.
                    if (!restarted || !state.StartedByRule)
                    {
                        events.Add(new TriggerEvent(rule, TriggerEventKind.DeviceConnected));
                        OnStarted(rule, state, activeProfileId, actions, events);
                    }
                }
            }
            else if (state.IsRunning)
            {
                state.IsRunning = false;
                state.GoneSince = now;
                state.PollsGone = 1;
                events.Add(new TriggerEvent(rule, TriggerEventKind.DeviceGone) { Delay = ExitDelayOf(rule) });
            }
            else if (state.GoneSince is not null)
            {
                state.PollsGone++;
            }
            else
            {
                // Gone and waiting for a retry that no longer applies.
                state.RetryAt = null;
            }

            if (!running && state.GoneSince is { } gone && state.PollsGone >= PollsGoneForExit && now - gone >= ExitDelayOf(rule))
            {
                // Only an end action that would switch is worth holding; a skipped one is reported as skipped right away.
                bool wouldSwitch = state.StartedByRule && activeProfileId == rule.ProfileId && rule.OnExit != ExitAction.Stay;
                if (wouldSwitch && (blocked ??= exitBlocked?.Invoke() ?? false))
                {
                    if (!state.ExitHeld)
                    {
                        state.ExitHeld = true;
                        events.Add(new TriggerEvent(rule, TriggerEventKind.ExitHeld));
                    }

                    continue;
                }

                state.ExitHeld = false;
                state.GoneSince = null;
                state.PollsGone = 0;
                ExitSkipReason skipped = OnExited(rule, state, activeProfileId, actions);
                if (skipped != ExitSkipReason.None)
                {
                    events.Add(new TriggerEvent(rule, TriggerEventKind.ExitSkipped) { SkipReason = skipped });
                }
            }
        }

        _hasBaseline = true;
        return new TriggerEvaluation(actions, events);
    }

    private static void OnStarted(
        AutomationRule rule, RuleState state, Guid? activeProfileId, List<TriggerAction> actions, List<TriggerEvent> events)
    {
        state.StartedByRule = true;
        state.PreviousProfileId = activeProfileId == rule.ProfileId ? null : activeProfileId;
        if (rule.OnExit == ExitAction.SwitchBack && state.PreviousProfileId is null)
        {
            events.Add(new TriggerEvent(rule, TriggerEventKind.NoPreviousProfile));
        }

        if (activeProfileId != rule.ProfileId)
        {
            actions.Add(new TriggerAction(rule, rule.ProfileId, TriggerReason.Started));
        }
    }

    private static ExitSkipReason OnExited(AutomationRule rule, RuleState state, Guid? activeProfileId, List<TriggerAction> actions)
    {
        bool startedByRule = state.StartedByRule;
        Guid? previous = state.PreviousProfileId;
        state.StartedByRule = false;
        state.PreviousProfileId = null;

        if (!startedByRule)
        {
            return ExitSkipReason.NotStartedByRule;
        }

        if (activeProfileId != rule.ProfileId)
        {
            return ExitSkipReason.OtherProfileActive;
        }

        Guid? target = rule.OnExit switch
        {
            ExitAction.SwitchBack => previous,
            ExitAction.SwitchTo => rule.ExitProfileId,
            _ => null,
        };

        if (target is not { } profile)
        {
            return rule.OnExit == ExitAction.SwitchBack ? ExitSkipReason.NoPreviousProfile : ExitSkipReason.Stay;
        }

        if (profile == activeProfileId)
        {
            return ExitSkipReason.TargetActive;
        }

        actions.Add(new TriggerAction(rule, profile, TriggerReason.Ended));
        return ExitSkipReason.None;
    }

    private sealed class RuleState
    {
        public bool IsRunning { get; set; }

        /// <summary>The device key the state was built for.</summary>
        public required string Watched { get; init; }

        /// <summary>Monotonic time the device was first missed.</summary>
        public TimeSpan? GoneSince { get; set; }

        /// <summary>Polls in a row without the device since <see cref="GoneSince"/>.</summary>
        public int PollsGone { get; set; }

        /// <summary>The end action is due but waits for a full-screen application to close; reported once.</summary>
        public bool ExitHeld { get; set; }

        /// <summary>A start that could not run is not retried before this time.</summary>
        public TimeSpan? RetryAt { get; set; }

        /// <summary>The device connected while the rule was enabled and watched, so its exit may switch.</summary>
        public bool StartedByRule { get; set; }

        public Guid? PreviousProfileId { get; set; }
    }
}
