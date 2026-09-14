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

    /// <summary>Cancelled when the app exits; a running switch rolls back and ends (analysis finding B-02).</summary>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>The switch or catch-up running now, if any. UI thread only.</summary>
    private Task? _current;

    /// <summary>Last switch applied partially: the profile and how many displays it got. Cleared by any other switch.</summary>
    private (Profile Profile, int Displays)? _pendingCatchUp;

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

    public void Dispose()
    {
        _gate.Dispose();
        _stopping.Dispose();
    }

    /// <summary>
    /// Cancels a running switch – a pending confirmation rolls back – and waits for it to end, at most
    /// <paramref name="timeout"/>. Returns false if it is still running then.
    /// </summary>
    public async Task<bool> StopAsync(TimeSpan timeout)
    {
        await _stopping.CancelAsync();
        if (_current is not { IsCompleted: false } current)
        {
            return true;
        }

        _log.Information("Waiting up to {Seconds} s for the running switch to end", timeout.TotalSeconds);
        Task finished = await Task.WhenAny(current, Task.Delay(timeout, _time));
        return finished == current;
    }

    public async Task SwitchAsync(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await RunAsync(profile, new SwitchRequest(), rethrow: false);
    }

    /// <summary>Switch with per-call options, e.g. an automation rule that skips the confirmation.</summary>
    /// <returns>The result, or <c>null</c> when another switch was running, it was cancelled or it threw.</returns>
    public Task<SwitchResult?> SwitchAsync(Profile profile, SwitchRequest request)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(profile, request, rethrow: false);
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
        return RunAsync(profile, request, rethrow: true, cancellationToken);
    }

    private async Task<SwitchResult?> RunAsync(Profile profile, SwitchRequest request, bool rethrow, CancellationToken cancellationToken = default)
    {
        bool dryRun = request.DryRun;
        if (_stopping.IsCancellationRequested)
        {
            _log.Information("Switch to {Profile} ignored, RigShift is exiting", profile.Name);
            return null;
        }

        if (!await _gate.WaitAsync(0, CancellationToken.None))
        {
            _log.Information("Switch to {Profile} ignored, another switch is running", profile.Name);
            BusyRejected?.Invoke(this, EventArgs.Empty);
            return null;
        }

        IsSwitching = true;
        DateTimeOffset started = _time.GetLocalNow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        try
        {
            SwitchRequest effective = request with { DefaultConfirmTimeoutSeconds = _settings.Current.ConfirmTimeoutSeconds };
            Task<SwitchResult> running = Task.Run(() => _orchestrator.SwitchAsync(profile, effective, linked.Token), CancellationToken.None);
            _current = running;
            SwitchResult result = await running;

            if (!dryRun)
            {
                RememberCatchUp(profile, result);
                Complete(ToRecord(started, profile, result));
            }

            return result;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _log.Information("Switch to {Profile} cancelled", profile.Name);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The orchestrator reports expected failures as results; anything thrown is a bug or an OS surprise.
            _log.Error(ex, "Switch to {Profile} threw", profile.Name);
            if (!dryRun)
            {
                Complete(new SwitchRecord(started, profile.Name, SwitchOutcome.Failed, AudioOutcome.NotConfigured, AppsOutcome.NotConfigured, 0,
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

    /// <summary>
    /// Call after the active profile was refreshed on a display change. If the last switch skipped optional displays and
    /// its profile is still active, re-applies it once more of them are there. No time limit: a spacedesk viewer often
    /// connects minutes after the switch (deviation from the 60 s in PLAN 4.3, decided in M5).
    /// </summary>
    public async Task CatchUpAsync()
    {
        if (_pendingCatchUp is not { } pending)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        IsSwitching = true;
        DateTimeOffset started = _time.GetLocalNow();
        try
        {
            // The active profile does not decide: when the missing display connects, Windows itself may restore whatever
            // layout its database holds for that set of monitors (M5 2026-09-13 20:17: spacedesk connected → Desk).
            Task<SwitchResult?> running = Task.Run(() => _orchestrator.CatchUpAsync(pending.Profile, pending.Displays, _stopping.Token), CancellationToken.None);
            _current = running;
            SwitchResult? result = await running;
            if (result is not null)
            {
                RememberCatchUp(pending.Profile, result);
                Complete(ToRecord(started, pending.Profile, result));
            }
            else if (_catalog.ActiveProfile is { } active && active.Id != pending.Profile.Id)
            {
                // Changed to another profile outside RigShift without the missing display showing up: stop following.
                _log.Information("Catch-up for {Profile} dropped, {Active} is active now", pending.Profile.Name, active.Name);
                _pendingCatchUp = null;
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            _log.Information("Catch-up of {Profile} cancelled", pending.Profile.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Catch-up of {Profile} threw", pending.Profile.Name);
            _pendingCatchUp = null;
        }
        finally
        {
            IsSwitching = false;
            _gate.Release();
        }
    }

    private void RememberCatchUp(Profile profile, SwitchResult result) =>
        _pendingCatchUp = result.Outcome == SwitchOutcome.AppliedPartially ? (profile, result.Plan.Resolved.Count)
            : result.Outcome == SwitchOutcome.Failed && _pendingCatchUp?.Profile.Id == profile.Id ? _pendingCatchUp
            : null;

    private static SwitchRecord ToRecord(DateTimeOffset started, Profile profile, SwitchResult result) =>
        new(started, profile.Name, result.Outcome, result.Audio, result.Apps, result.Attempts, result.Duration, result.LastNativeError, result.Message,
            result.Plan.Missing.Select(m => SwitchMessages.NameOf(m.Assignment)).ToList(),
            profile.AppsWaitForUsbDeviceName ?? profile.AppsWaitForUsbDeviceId, Profile.ClampAppsWaitSeconds(profile.AppsWaitSeconds), result.Note);

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
