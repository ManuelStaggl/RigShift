using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// Runs switches one at a time off the UI thread, keeps the recent history and tells the tray about results.
/// </summary>
public sealed partial class SwitchCoordinator : ObservableObject, IDisposable, IProfileSwitcher
{
    private const int HistoryLength = 10;

    private readonly SwitchOrchestrator _orchestrator;
    private readonly ProfileCatalog _catalog;
    private readonly SettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SwitchCoordinator(SwitchOrchestrator orchestrator, ProfileCatalog catalog, SettingsService settings, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _orchestrator = orchestrator;
        _catalog = catalog;
        _settings = settings;
        _time = time;
        _log = log.ForContext<SwitchCoordinator>();
    }

    public event EventHandler<SwitchRecord>? SwitchCompleted;

    public event EventHandler? BusyRejected;

    public ObservableCollection<SwitchRecord> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsSwitching { get; set; }

    public bool IsIdle => !IsSwitching;

    public void Dispose() => _gate.Dispose();

    public async Task SwitchAsync(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await RunAsync(profile, new SwitchRequest(), rethrow: false);
    }

    /// <summary>Dry run: plans against the live topology without touching anything.</summary>
    public Task<SwitchResult?> CheckAsync(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return RunAsync(profile, new SwitchRequest { DryRun = true }, rethrow: false);
    }

    /// <summary>Command line: the caller needs the exception to report a failure instead of "busy".</summary>
    Task<SwitchResult?> IProfileSwitcher.SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(profile, request, rethrow: true);
    }

    private async Task<SwitchResult?> RunAsync(Profile profile, SwitchRequest request, bool rethrow)
    {
        bool dryRun = request.DryRun;
        if (!await _gate.WaitAsync(0))
        {
            _log.Information("Switch to {Profile} ignored, another switch is running", profile.Name);
            BusyRejected?.Invoke(this, EventArgs.Empty);
            return null;
        }

        IsSwitching = true;
        DateTimeOffset started = _time.GetLocalNow();
        try
        {
            SwitchRequest effective = request with { DefaultConfirmTimeoutSeconds = _settings.Current.ConfirmTimeoutSeconds };
            SwitchResult result = await Task.Run(() => _orchestrator.SwitchAsync(profile, effective, CancellationToken.None));

            if (!dryRun)
            {
                Complete(new SwitchRecord(started, profile.Name, result.Outcome, result.Audio, result.Attempts, result.Duration,
                    result.LastNativeError, result.Message,
                    result.Plan.Missing.Select(m => SwitchMessages.NameOf(m.Assignment.Identity)).ToList()));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The orchestrator reports expected failures as results; anything thrown is a bug or an OS surprise.
            _log.Error(ex, "Switch to {Profile} threw", profile.Name);
            if (!dryRun)
            {
                Complete(new SwitchRecord(started, profile.Name, SwitchOutcome.Failed, AudioOutcome.NotConfigured, 0,
                    _time.GetLocalNow() - started, null, ex.Message, []));
            }

            if (rethrow)
            {
                throw;
            }

            return null;
        }
        finally
        {
            IsSwitching = false;
            _gate.Release();
        }
    }

    private void Complete(SwitchRecord record)
    {
        History.Insert(0, record);
        while (History.Count > HistoryLength)
        {
            History.RemoveAt(History.Count - 1);
        }

        SwitchCompleted?.Invoke(this, record);
        _ = _catalog.RefreshActiveAsync(CancellationToken.None);
    }
}
