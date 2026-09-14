using System.ComponentModel;
using System.Windows.Threading;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Polls the running programs every 2 seconds and switches when a rule's game starts or closes (docs/PLAN.md,
/// section 6). Polling needs no administrator rights, unlike WMI process events.
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IProcessList _processes;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly ProcessTrigger _trigger = new();
    private readonly DispatcherTimer _timer = new() { Interval = PollInterval };
    private bool _polling;
    private bool _idle = true;

    public AutomationService(
        SettingsService settings, ProfileCatalog catalog, SwitchCoordinator coordinator, IProcessList processes, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _coordinator = coordinator;
        _processes = processes;
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
            IReadOnlySet<string> running = await Task.Run(_processes.RunningProcessNames);

            // While a switch runs, the active profile is in flux; the next poll sees the same processes again.
            if (_coordinator.IsSwitching)
            {
                return;
            }

            foreach (TriggerAction action in _trigger.Evaluate(rules, running, _catalog.ActiveProfile?.Id, _time.GetUtcNow()))
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
        string game = GameTemplates.Find(action.Rule.TemplateId)?.Name ?? action.Rule.ExecutablePath ?? "?";
        if (_catalog.Find(action.ProfileId) is not { } profile)
        {
            _log.Warning("Rule for {Game} wants profile {ProfileId}, which does not exist", game, action.ProfileId);
            return;
        }

        _log.Information("{Game} {Reason}: switching to {Profile} (skip confirmation: {SkipConfirmation})",
            game, action.Reason == TriggerReason.GameStarted ? "started" : "closed", profile.Name, action.SkipConfirmation);
        await _coordinator.SwitchAsync(profile, new SwitchRequest { SkipConfirmation = action.SkipConfirmation });
    }
}
