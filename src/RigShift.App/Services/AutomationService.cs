using System.ComponentModel;
using System.Windows.Threading;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Polls the running programs and connected USB devices every 2 seconds and switches when a rule's game starts or closes
/// or its device connects or disappears (docs/PLAN.md, section 6). Polling needs no administrator rights, unlike WMI
/// process events.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IProcessList _processes;
    private readonly IUsbDeviceList _devices;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly AutomationTrigger _trigger = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private bool _polling;
    private bool _idle = true;

    public AutomationService(
        SettingsService settings,
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        IProcessList processes,
        IUsbDeviceList devices,
        TimeProvider time,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _coordinator = coordinator;
        _processes = processes;
        _devices = devices;
        _time = time;
        _log = log.ForContext<AutomationService>();
        _timer.Tick += OnTick;
        settings.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Rules or the paused state may have changed.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<AutomationRule> Rules => _settings.Current.AutomationRules ?? [];

    public bool IsPaused => _settings.Current.AutomationPaused;

    public void Start()
    {
        _timer.Start();
        _log.Information("Automation started with {Count} rule(s), paused {Paused}", Rules.Count, IsPaused);
    }

    public async Task SetPausedAsync(bool paused)
    {
        await _settings.UpdateAsync(s => s with { AutomationPaused = paused }, CancellationToken.None);
        _log.Information("Automation {State}", paused ? "paused" : "resumed");
    }

    public void Dispose() => _timer.Stop();

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_polling)
        {
            return;
        }

        IReadOnlyList<AutomationRule> rules = Rules;
        if (IsPaused || rules.Count == 0)
        {
            // Resuming must not treat a game that started meanwhile as a fresh start.
            if (!_idle)
            {
                _trigger.Reset();
                _idle = true;
            }

            return;
        }

        _idle = false;
        _polling = true;
        try
        {
            IReadOnlySet<string> present = await Task.Run(() => Present(rules));

            // While a switch runs, the active profile is in flux; the next poll sees the same processes again.
            if (_coordinator.IsSwitching)
            {
                return;
            }

            foreach (TriggerAction action in _trigger.Evaluate(rules, present, _catalog.ActiveProfile?.Id, _time.GetUtcNow()))
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

    /// <summary>Only asks the OS for what the rules watch: processes for game rules, devices for USB rules.</summary>
    private HashSet<string> Present(IReadOnlyList<AutomationRule> rules)
    {
        var present = new HashSet<string>(ProcessNames.Comparer);
        if (rules.Any(r => r.UsbDeviceId is null))
        {
            present.UnionWith(_processes.RunningProcessNames());
        }

        if (rules.Any(r => r.UsbDeviceId is not null))
        {
            present.UnionWith(_devices.PresentDeviceIds().Select(UsbDeviceIds.Key));
        }

        return present;
    }

    private async Task RunAsync(TriggerAction action)
    {
        AutomationRule rule = action.Rule;
        bool usb = rule.UsbDeviceId is not null;
        string subject = usb
            ? rule.UsbDeviceName ?? rule.UsbDeviceId ?? "?"
            : GameTemplates.Find(rule.TemplateId)?.Name ?? rule.ExecutablePath ?? "?";
        if (_catalog.Find(action.ProfileId) is not { } profile)
        {
            _log.Warning("Rule for {Subject} wants profile {ProfileId}, which does not exist", subject, action.ProfileId);
            return;
        }

        string reason = (usb, action.Reason) switch
        {
            (true, TriggerReason.Started) => "connected",
            (true, _) => "disconnected",
            (false, TriggerReason.Started) => "started",
            _ => "closed",
        };
        _log.Information("{Subject} {Reason}: switching to {Profile} (skip confirmation: {SkipConfirmation})",
            subject, reason, profile.Name, action.SkipConfirmation);
        await _coordinator.SwitchAsync(profile, new SwitchRequest { SkipConfirmation = action.SkipConfirmation });
    }
}
