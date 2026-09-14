namespace RigShift.Core.Automation;

public enum TriggerReason
{
    GameStarted,
    GameExited,
}

/// <summary>A switch the automation asks for.</summary>
public sealed record TriggerAction(AutomationRule Rule, Guid ProfileId, TriggerReason Reason)
{
    public bool SkipConfirmation => Rule.SkipConfirmation;
}

/// <summary>
/// Decides from polled process lists when rules switch (docs/PLAN.md, section 6). Pure logic: the caller supplies the
/// running processes, the active profile and the time.
/// </summary>
/// <remarks>
/// Start: a game that appears switches to the rule's profile, unless it is active already. Processes running at the first
/// poll only set the baseline, so starting RigShift next to a running game changes nothing.
/// Exit: acted on once the game has been gone for <see cref="ExitDelay"/> (a restart in between is no exit), and only
/// while the rule's profile is still active – a profile the user picked in the meantime is not overridden.
/// </remarks>
public sealed class ProcessTrigger
{
    private readonly Dictionary<Guid, RuleState> _states = [];
    private bool _hasBaseline;

    public TimeSpan ExitDelay { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Forget everything, e.g. after pausing: the next poll sets a new baseline.</summary>
    public void Reset()
    {
        _states.Clear();
        _hasBaseline = false;
    }

    public IReadOnlyList<TriggerAction> Evaluate(
        IReadOnlyList<AutomationRule> rules, IReadOnlySet<string> runningProcesses, Guid? activeProfileId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(runningProcesses);

        foreach (Guid removed in _states.Keys.Where(id => rules.All(r => r.Id != id)).ToList())
        {
            _states.Remove(removed);
        }

        var actions = new List<TriggerAction>();
        foreach (AutomationRule rule in rules)
        {
            IReadOnlyList<string> names = GameTemplates.ProcessNamesOf(rule);
            bool running = names.Any(runningProcesses.Contains);
            string watched = string.Join('|', names.Order(ProcessNames.Comparer)).ToUpperInvariant();
            if (!_states.TryGetValue(rule.Id, out RuleState? state) || state.Watched != watched)
            {
                // A new rule, or one whose game was changed while that game runs, behaves like the baseline: no switch
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

            if (!running && state.GoneSince is { } gone && now - gone >= ExitDelay)
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
            actions.Add(new TriggerAction(rule, rule.ProfileId, TriggerReason.GameStarted));
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
            actions.Add(new TriggerAction(rule, profile, TriggerReason.GameExited));
        }
    }

    private sealed class RuleState
    {
        public bool IsRunning { get; set; }

        /// <summary>The process names the state was built for.</summary>
        public required string Watched { get; init; }

        public DateTimeOffset? GoneSince { get; set; }

        /// <summary>The game started while the rule was enabled and watched, so its exit may switch.</summary>
        public bool StartedByRule { get; set; }

        public Guid? PreviousProfileId { get; set; }
    }
}
