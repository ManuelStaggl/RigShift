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

/// <summary>
/// Decides from polled USB devices when rules switch (docs/PLAN.md, section 6). Pure logic: the caller supplies what is
/// present, the active profile and the time.
/// </summary>
/// <remarks>
/// Start: a device that appears switches to the rule's profile, unless it is active already. Whatever is present at the
/// first poll only sets the baseline, so starting RigShift with the device already connected changes nothing.
/// End: acted on once the device has been gone for the rule's <see cref="ExitDelayOf"/> (a restart in between is no end), and
/// only while the rule's profile is still active – a profile the user picked in the meantime is not overridden.
/// </remarks>
public sealed class AutomationTrigger
{
    private readonly Dictionary<Guid, RuleState> _states = [];
    private bool _hasBaseline;

    /// <summary>The rule's <see cref="AutomationRule.ExitDelaySeconds"/>, kept between 0 and 10 minutes.</summary>
    public static TimeSpan ExitDelayOf(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return TimeSpan.FromSeconds(Math.Clamp(rule.ExitDelaySeconds, 0, AutomationRule.MaxExitDelaySeconds));
    }

    /// <summary>Forget everything, e.g. after pausing: the next poll sets a new baseline.</summary>
    public void Reset()
    {
        _states.Clear();
        _hasBaseline = false;
    }

    /// <summary>
    /// What a rule watches in the present set: <see cref="UsbDeviceIds.Key"/> of its device; nothing for a rule without a
    /// valid device id.
    /// </summary>
    public static IReadOnlyList<string> WatchedKeysOf(AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        return UsbDeviceIds.Normalize(rule.UsbDeviceId) is { } id ? [UsbDeviceIds.Key(id)] : [];
    }

    /// <param name="present"><see cref="UsbDeviceIds.Key"/> of connected devices.</param>
    public IReadOnlyList<TriggerAction> Evaluate(
        IReadOnlyList<AutomationRule> rules, IReadOnlySet<string> present, Guid? activeProfileId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(present);

        foreach (Guid removed in _states.Keys.Where(id => rules.All(r => r.Id != id)).ToList())
        {
            _states.Remove(removed);
        }

        var actions = new List<TriggerAction>();
        foreach (AutomationRule rule in rules)
        {
            // Rules without a device (game rules of an unreleased build) are ignored.
            if (rule.UsbDeviceId is null)
            {
                continue;
            }

            IReadOnlyList<string> keys = WatchedKeysOf(rule);
            bool running = keys.Any(present.Contains);
            string watched = string.Join('|', keys.Order(StringComparer.OrdinalIgnoreCase)).ToUpperInvariant();
            if (!_states.TryGetValue(rule.Id, out RuleState? state) || state.Watched != watched)
            {
                // A new rule, or one whose device was changed while it is present, behaves like the baseline: no switch
                // until the next start.
                _states[rule.Id] = new RuleState { IsRunning = running, Watched = watched };
                continue;
            }

            if (!_hasBaseline)
            {
                state.IsRunning = running;
                continue;
            }

            if (running)
            {
                bool restarted = state.GoneSince is not null;
                state.GoneSince = null;
                if (!state.IsRunning)
                {
                    state.IsRunning = true;
                    if (!restarted)
                    {
                        OnStarted(rule, state, activeProfileId, actions);
                    }
                }
            }
            else if (state.IsRunning)
            {
                state.IsRunning = false;
                state.GoneSince = now;
            }

            if (!running && state.GoneSince is { } gone && now - gone >= ExitDelayOf(rule))
            {
                state.GoneSince = null;
                OnExited(rule, state, activeProfileId, actions);
            }
        }

        _hasBaseline = true;
        return actions;
    }

    private static void OnStarted(AutomationRule rule, RuleState state, Guid? activeProfileId, List<TriggerAction> actions)
    {
        if (!rule.IsEnabled)
        {
            state.StartedByRule = false;
            return;
        }

        state.StartedByRule = true;
        state.PreviousProfileId = activeProfileId == rule.ProfileId ? null : activeProfileId;
        if (activeProfileId != rule.ProfileId)
        {
            actions.Add(new TriggerAction(rule, rule.ProfileId, TriggerReason.Started));
        }
    }

    private static void OnExited(AutomationRule rule, RuleState state, Guid? activeProfileId, List<TriggerAction> actions)
    {
        bool startedByRule = state.StartedByRule;
        Guid? previous = state.PreviousProfileId;
        state.StartedByRule = false;
        state.PreviousProfileId = null;

        if (!startedByRule || !rule.IsEnabled || activeProfileId != rule.ProfileId)
        {
            return;
        }

        Guid? target = rule.OnExit switch
        {
            ExitAction.SwitchBack => previous,
            ExitAction.SwitchTo => rule.ExitProfileId,
            _ => null,
        };

        if (target is { } profile && profile != activeProfileId)
        {
            actions.Add(new TriggerAction(rule, profile, TriggerReason.Ended));
        }
    }

    private sealed class RuleState
    {
        public bool IsRunning { get; set; }

        /// <summary>The device key the state was built for.</summary>
        public required string Watched { get; init; }

        public DateTimeOffset? GoneSince { get; set; }

        /// <summary>The device connected while the rule was enabled and watched, so its exit may switch.</summary>
        public bool StartedByRule { get; set; }

        public Guid? PreviousProfileId { get; set; }
    }
}
