using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Brings a planned arrangement onto the screens: waiting for targets that are not ready, retrying, and putting a
/// previous arrangement back. The hard rules are in docs/display-topology.md. Logs under the orchestrator's context,
/// so the log source stays the same.
/// </summary>
internal sealed class TopologyApplier
{
    /// <summary>ERROR_GEN_FAILURE. In practice "target not ready yet", not "impossible" (display-topology.md, rule 4).</summary>
    private const int ErrorGenFailure = 31;

    /// <summary>
    /// ERROR_BAD_CONFIGURATION. Observed on the gaming PC (M5, 2026-09-13) when a sleeping ultrawide dropped off the bus
    /// mid-switch and reappeared seconds later – as transient as error 31.
    /// </summary>
    private const int ErrorBadConfiguration = 1610;

    /// <summary>Per retry cycle: stored modes first, then database modes (display-topology.md, rule 5).</summary>
    private static readonly bool[] ModeSources = [false, true];

    private readonly IDisplayConfigurator _display;
    private readonly TopologyPlanner _planner;
    private readonly HdrSwitcher _hdr;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    public TopologyApplier(IDisplayConfigurator display, TopologyPlanner planner, SwitchOptions options, TimeProvider time, ILogger log)
    {
        _display = display;
        _planner = planner;
        _options = options;
        _time = time;
        _log = log;
        _hdr = new HdrSwitcher(display, options, time, log);
    }

    /// <summary>
    /// Stored modes first, then database modes (rule 5). On a transient error (31, 1610): wait, re-query, re-plan and try again
    /// within the time budget (rule 4). Every retry uses a fresh snapshot because LUIDs may change (rule 2). HDR follows
    /// the arrangement that stayed.
    /// </summary>
    /// <param name="restoring">The previous arrangement comes back: displays Windows had duplicated are duplicated again.</param>
    public async Task<ApplyOutcome> ApplyAsync(
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
                    return ApplyOutcome.Failure(plan, attempts, lastError,
                        string.Create(CultureInfo.InvariantCulture, $"Gave up after {attempts} attempts (last error {lastError})."));
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
                    return ApplyOutcome.Failure(plan, attempts, lastError,
                        string.Create(CultureInfo.InvariantCulture,
                            $"The display driver did not return within {_options.ApplyCallTimeout.TotalSeconds} s."));
                }
                catch (Exception ex) when (DisplayApiFailure.Is(ex))
                {
                    _log.Error(ex, "Attempt {Attempt} threw ({ModeSource} modes)", attempts, modeSource);
                    return ApplyOutcome.Failure(plan, attempts, lastError, "The display configuration could not be applied: " + ex.Message);
                }

                double milliseconds = _time.GetElapsedTime(attemptStarted).TotalMilliseconds;
                if (code == 0)
                {
                    _log.Information("Attempt {Attempt} succeeded ({ModeSource} modes, {Displays} displays, {Milliseconds:0} ms)",
                        attempts, modeSource, plan.Resolved.Count, milliseconds);
                    await _hdr.SwitchAsync(profile, cancellationToken);
                    return new ApplyOutcome(true, plan, attempts, lastError, null, databaseModes);
                }

                lastError = code;
                transient |= code is ErrorGenFailure or ErrorBadConfiguration;
                _log.Warning("Attempt {Attempt} failed with native error {Error} ({ModeSource} modes, {Milliseconds:0} ms)",
                    attempts, code, modeSource, milliseconds);
            }

            if (!transient)
            {
                return ApplyOutcome.Failure(plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture, $"SetDisplayConfig failed with error {lastError}."));
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return ApplyOutcome.Failure(plan, attempts, lastError,
                    string.Create(CultureInfo.InvariantCulture,
                        $"A display did not become ready within {_options.TargetWaitBudget.TotalSeconds} s (error {lastError})."));
            }

            try
            {
                plan = await WaitForDisplaysAsync(profile, plan, deadline, force: true, includeDetached: true, cancellationToken);
            }
            catch (Exception ex) when (DisplayApiFailure.Is(ex))
            {
                _log.Error(ex, "Displays could not be queried while waiting for a retry");
                return ApplyOutcome.Failure(plan, attempts, lastError, "The displays could not be queried: " + ex.Message);
            }

            if (BlockReason(plan) is { } blocked)
            {
                return ApplyOutcome.Failure(plan, attempts, lastError, blocked);
            }
        }
    }

    /// <summary>
    /// Re-queries the topology while a required display is attached but not ready, until the deadline. With
    /// <paramref name="includeDetached"/> a required display that is not connected is waited for too: a waking monitor can
    /// drop off the bus for seconds, and a switched-off one may be switched on (HW-16). <paramref name="force"/> re-queries
    /// at least once.
    /// </summary>
    public async Task<TopologyPlan> WaitForDisplaysAsync(
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

    /// <summary>Why the plan cannot be applied, or <c>null</c> when it can.</summary>
    public static string? BlockReason(TopologyPlan plan)
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
    /// With database modes Windows decides the modes and may even leave a display dark; the stored plan says nothing
    /// about that. Compares a fresh snapshot with the plan (analysis finding B-11): dark displays count as missing,
    /// the note only stays when a mode differs.
    /// </summary>
    public async Task<ModeCheck> CheckDatabaseModesAsync(TopologyPlan plan, CancellationToken cancellationToken)
    {
        DisplaySnapshot now;
        try
        {
            now = await _display.QueryAsync(cancellationToken);
        }
        catch (Exception ex) when (DisplayApiFailure.Is(ex))
        {
            _log.Warning(ex, "Displays could not be queried to check the database modes");
            return new ModeCheck(plan, SwitchNote.ModesFromDatabase, DisplaysDark: false);
        }

        var dark = new List<PlannedDisplay>();
        bool modesDiffer = false;
        foreach (PlannedDisplay planned in plan.Resolved)
        {
            DisplayAssignment wanted = planned.Assignment;
            if (now.FindActive(planned.Target.Identity)?.ActiveMode is not { } mode)
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
    /// Puts the arrangement of <paramref name="before"/> back, planned against <paramref name="now"/>, HDR states included.
    /// Every display of it is optional: a partial restore beats none.
    /// </summary>
    public async Task<ApplyOutcome> RestoreAsync(DisplaySnapshot before, DisplaySnapshot now, CancellationToken cancellationToken)
    {
        Profile previous = PreviousTopology(before);
        TopologyPlan plan = _planner.Plan(previous, now);
        LogPlan(plan);
        if (plan.Resolved.Count == 0)
        {
            return ApplyOutcome.Failure(plan, 0, null, "None of the previously active displays is available.");
        }

        return await ApplyAsync(previous, plan, _time.GetUtcNow() + _options.TargetWaitBudget, cancellationToken, restoring: true);
    }

    /// <summary>The topology of a snapshot as a throwaway profile, every display optional.</summary>
    public static Profile PreviousTopology(DisplaySnapshot before)
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

    public void LogPlan(TopologyPlan plan)
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
}

/// <param name="UsedDatabaseModes">Windows chose the modes: what runs may differ from the plan (see <see cref="TopologyApplier.CheckDatabaseModesAsync"/>).</param>
internal sealed record ApplyOutcome(bool Succeeded, TopologyPlan Plan, int Attempts, int? LastNativeError, string? Message, bool UsedDatabaseModes)
{
    public static ApplyOutcome Failure(TopologyPlan plan, int attempts, int? lastNativeError, string message) =>
        new(false, plan, attempts, lastNativeError, message, UsedDatabaseModes: false);
}

/// <param name="Plan">The plan as it came out: displays that stayed dark moved to its missing ones.</param>
internal sealed record ModeCheck(TopologyPlan Plan, SwitchNote Note, bool DisplaysDark)
{
    public static ModeCheck AsPlanned(TopologyPlan plan) => new(plan, SwitchNote.None, DisplaysDark: false);
}
