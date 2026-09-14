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
/// (docs/PLAN.md, section 6). Polling needs no window and no administrator rights.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IUsbDeviceList _devices;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly AutomationTrigger _trigger = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };

    /// <summary>Origin of the monotonic time handed to the trigger: wall-clock jumps and sleep must not end a delay.</summary>
    private readonly long _started;
    private bool _polling;
    private bool _idle = true;
    private bool _skipLogged;

    public AutomationService(
        SettingsService settings,
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        IUsbDeviceList devices,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _coordinator = coordinator;
        _devices = devices;
        _time = time;
        _started = time.GetTimestamp();
        _log = log.ForContext<AutomationService>();
        _timer.Tick += OnTick;
        settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rules or the paused state may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Rules with a device key; a rule without one (written by an unreleased build) is dropped.</summary>
    public IReadOnlyList<AutomationRule> Rules => _settings.Current.AutomationRules?.Where(r => r.UsbDeviceId is not null).ToList() ?? [];

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
            TriggerEvaluation evaluation = _trigger.Evaluate(rules, present, _catalog.ActiveProfile?.Id, Now);
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
        SwitchResult? result = await _coordinator.SwitchAsync(profile, new SwitchRequest { SkipConfirmation = action.SkipConfirmation });
        if (action.Reason != TriggerReason.Started)
        {
            return;
        }

        // A start that did not succeed must not count as started (analysis finding C-01).
        RetryMode? retry = result?.Outcome switch
        {
            null or SwitchOutcome.Blocked => RetryMode.Later,
            SwitchOutcome.Failed or SwitchOutcome.RolledBack => RetryMode.AfterReconnect,
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
            case TriggerEventKind.ExitSkipped:
                _log.Information("{Subject} stayed gone, end action skipped: {Reason}", subject, triggerEvent.SkipReason);
                break;
            case TriggerEventKind.Disarmed when triggerEvent.Retry == RetryMode.Later:
                _log.Information("Rule for {Subject} retries in {Seconds} s if the device is still connected", subject, triggerEvent.Delay?.TotalSeconds ?? 0);
                break;
            case TriggerEventKind.Disarmed:
                _log.Information("Rule for {Subject} starts again once the device reconnects", subject);
                break;
        }
    }

    private static string SubjectOf(AutomationRule rule) => rule.UsbDeviceName ?? rule.UsbDeviceId ?? "?";
}
