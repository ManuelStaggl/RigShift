using System.ComponentModel;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Polls the connected USB devices every 2 seconds and switches when a rule's device connects or disappears
///. Polling needs no window and no administrator rights.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    /// <summary>ERROR_ACCESS_DENIED: the display calls of a session without its desktop.</summary>
    private const int ErrorAccessDenied = 5;

    /// <summary>Shortest countdown after a rule switched: the wheelbase is on, its owner not yet in the seat (U-04).</summary>
    internal const int RuleConfirmSeconds = 30;

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IUsbDeviceList _devices;
    private readonly IFullscreenCheck _fullscreen;
    private readonly ISessionWatch _session;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly AutomationTrigger _trigger = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    /// <summary>Origin of the monotonic time handed to the trigger: wall-clock jumps and sleep must not end a delay.</summary>
    private readonly long _started;
    private bool _polling;
    private bool _idle = true;
    private bool _skipLogged;
    private bool _awayLogged;

    public AutomationService(
        SettingsService settings,
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        IUsbDeviceList devices,
        IFullscreenCheck fullscreen,
        ISessionWatch session,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _coordinator = coordinator;
        _devices = devices;
        _fullscreen = fullscreen;
        _session = session;
        _time = time;
        _started = time.GetTimestamp();
        _log = log.ForContext<AutomationService>();
        _timer.Tick += OnTick;
        settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        session.Changed += OnSessionChanged;
    }

    /// <summary>Rules or the paused state may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Rules with a device list; a rule without one (written by an unreleased build) is dropped.</summary>
    public IReadOnlyList<AutomationRule> Rules =>
        _settings.Current.AutomationRules?.Where(r => !AutomationTrigger.IsIgnored(r)).Select(r => r.Migrated()).ToList() ?? [];

    public bool IsPaused => _settings.Current.AutomationPaused;

    private TimeSpan Now => _time.GetElapsedTime(_started);

    public void Start()
    {
        _timer.Start();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _log.Information("Automation started with {Count} rule(s), paused {Paused}", Rules.Count, IsPaused);
    }

    /// <returns><c>false</c> if the settings could not be saved; the paused state stays as it was (analysis finding A-07).</returns>
    public async Task<bool> SetPausedAsync(bool paused)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { AutomationPaused = paused }, CancellationToken.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Automation could not be {State}", paused ? "paused" : "resumed");
            return false;
        }

        _log.Information("Automation {State}", paused ? "paused" : "resumed");
        return true;
    }

    public void Dispose()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _session.Changed -= OnSessionChanged;
    }

    /// <summary>
    /// Back in the session: look at the devices at once. What happened while it was locked is decided now – a wheelbase
    /// switched on then starts its rule, one switched off ends it (A-04).
    /// </summary>
    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (!_session.IsInteractive)
        {
            return;
        }

        _timer.Dispatcher.InvokeAsync(async () =>
        {
            _log.Information("Session is interactive again, automation looks at the devices");
            await PollAsync();
        });
    }

    /// <summary>
    /// After sleep the devices are re-enumerated and the timer did not tick: a device turned off before sleeping must not
    /// switch back on the first poll, so the next poll sets a new baseline (analysis finding C-03).
    /// </summary>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume)
        {
            return;
        }

        _timer.Dispatcher.InvokeAsync(() =>
        {
            _trigger.Reset();
            _log.Information("Resumed from sleep, automation takes a new baseline");
        });
    }

    private async void OnTick(object? sender, EventArgs e) => await PollAsync();

    /// <summary>One poll: what the timer runs every 2 seconds; tests call it directly.</summary>
    internal async Task PollAsync()
    {
        if (_polling)
        {
            return;
        }

        IReadOnlyList<AutomationRule> rules = Rules;
        if (IsPaused || rules.Count == 0)
        {
            // Resuming must not treat a device that connected meanwhile as a fresh start.
            if (!_idle)
            {
                _trigger.Reset();
                _idle = true;
                _log.Information("Automation idle ({Reason}), baseline reset", IsPaused ? "paused" : "no rules");
            }

            return;
        }

        // Locked or away from the console: every display call would fail with "access denied". The devices are looked at
        // again once the session is back, with the rules' state as it was before (A-04).
        if (!_session.IsInteractive)
        {
            if (!_awayLogged)
            {
                _log.Information("Automation waits while the session is locked or disconnected");
                _awayLogged = true;
            }

            return;
        }

        _awayLogged = false;
        _idle = false;
        _polling = true;
        try
        {
            IReadOnlySet<string> present = await Task.Run(_devices.PresentDeviceIds);

            // While a switch runs, the active profile is in flux; the next poll sees the same devices again.
            if (_coordinator.IsSwitching)
            {
                if (!_skipLogged)
                {
                    _log.Information("Automation poll skipped while a switch is running");
                    _skipLogged = true;
                }

                return;
            }

            _skipLogged = false;
            // Asked only when an end action is due: a full-screen game holds it back (1.7.0).
            TriggerEvaluation evaluation = _trigger.Evaluate(rules, present, _catalog.ActiveProfile?.Id, Now, _fullscreen.IsFullscreenAppRunning);
            foreach (TriggerEvent triggerEvent in evaluation.Events)
            {
                LogEvent(triggerEvent);
            }

            foreach (TriggerAction action in evaluation.Actions)
            {
                await RunAsync(action);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Automation poll failed");
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task RunAsync(TriggerAction action)
    {
        AutomationRule rule = action.Rule;
        string subject = SubjectOf(rule);
        if (_catalog.Find(action.ProfileId) is not { } profile)
        {
            _log.Warning("Rule for {Subject} wants profile {ProfileId}, which does not exist", subject, action.ProfileId);
            return;
        }

        string reason = action.Reason == TriggerReason.Started ? "connected" : "disconnected";
        _log.Information("{Subject} {Reason}: switching to {Profile} (skip confirmation: {SkipConfirmation})",
            subject, reason, profile.Name, action.SkipConfirmation);
        var request = new SwitchRequest { SkipConfirmation = action.SkipConfirmation, MinimumConfirmTimeoutSeconds = RuleConfirmSeconds };
        SwitchResult? result = await _coordinator.SwitchAsync(profile, request);
        if (action.Reason != TriggerReason.Started)
        {
            return;
        }

        // A start that did not succeed must not count as started (analysis finding C-01).
        // "Access denied" means the session lost the desktop while switching (locked): try again later, not only after a
        // reconnect of the device (A-04).
        RetryMode? retry = result switch
        {
            null or { Outcome: SwitchOutcome.Blocked } or { Outcome: SwitchOutcome.Failed, LastNativeError: ErrorAccessDenied } => RetryMode.Later,
            { Outcome: SwitchOutcome.Failed or SwitchOutcome.RolledBack } => RetryMode.AfterReconnect,
            _ => null,
        };

        if (retry is { } mode && _trigger.Disarm(rule, mode, Now) is { } disarmed)
        {
            _log.Information("Rule for {Subject} disarmed after {Outcome}", subject, result?.Outcome.ToString() ?? "no switch");
            LogEvent(disarmed);
        }
    }

    private void LogEvent(TriggerEvent triggerEvent)
    {
        string subject = SubjectOf(triggerEvent.Rule);
        switch (triggerEvent.Kind)
        {
            case TriggerEventKind.Baseline:
                _log.Information("Automation baseline for {Subject}: {State}", subject, triggerEvent.DevicePresent == true ? "connected" : "not connected");
                break;
            case TriggerEventKind.DeviceConnected:
                _log.Information("{Subject} connected", subject);
                break;
            case TriggerEventKind.DeviceGone:
                _log.Information("{Subject} gone, end action in {Seconds} s unless it comes back", subject, triggerEvent.Delay?.TotalSeconds ?? 0);
                break;
            case TriggerEventKind.DeviceBack:
                _log.Information("{Subject} back within its delay, nothing to do", subject);
                break;
            case TriggerEventKind.NoPreviousProfile:
                _log.Information("{Subject}: no other profile was active, so switching back when it is gone will do nothing", subject);
                break;
            case TriggerEventKind.ExitSkipped:
                _log.Information("{Subject} stayed gone, end action skipped: {Reason}", subject, triggerEvent.SkipReason);
                break;
            case TriggerEventKind.ExitHeld:
                _log.Information("{Subject} stayed gone, but a full-screen app is running: end action waits until it closes", subject);
                break;
            case TriggerEventKind.Disarmed when triggerEvent.Retry == RetryMode.Later:
                _log.Information("Rule for {Subject} retries in {Seconds} s if the device is still connected", subject, triggerEvent.Delay?.TotalSeconds ?? 0);
                break;
            case TriggerEventKind.Disarmed:
                _log.Information("Rule for {Subject} starts again once the device reconnects", subject);
                break;
        }
    }

    private string SubjectOf(AutomationRule rule) => UsbDeviceNames.Describe(rule, _settings.Current.UsbDeviceNames) ?? "?";
}
