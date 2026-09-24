using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.Core.Abstractions;
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

    /// <summary>
    /// The thread this was created on – the UI thread in the app, none in most tests. Everything that touches
    /// <see cref="IsSwitching"/>, <see cref="History"/> and the events runs there, whoever calls: a game session
    /// switches from a pool thread, and bound collections and the tray cannot be touched from one.
    /// </summary>
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly int _uiThread = Environment.CurrentManagedThreadId;

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
        if (_ui is null)
        {
            _log.Debug("Created without a synchronization context; switches run on the calling thread");
        }

        orchestrator.WaitingForDisplays += (_, displays) =>
            WaitingForDisplays?.Invoke(this, [.. displays.Select(d => SwitchMessages.NameOf(d))]);
    }

    public event EventHandler<SwitchRecord>? SwitchCompleted;

    /// <summary>
    /// A switch waits for required displays that are not connected; carries their names (finding HW-16). Raised on the
    /// switch's thread pool thread.
    /// </summary>
    public event EventHandler<IReadOnlyList<string>>? WaitingForDisplays;

    public event EventHandler? BusyRejected;

    /// <summary>
    /// Windows restored a profile's displays by itself and the profile has more than displays (finding HW-15). The tray
    /// offers to apply the rest with <see cref="SwitchRequest.KeepDisplays"/>.
    /// </summary>
    public event EventHandler<Profile>? RestoredByWindows;

    /// <summary>
    /// The apps of a switch ended after its result: the history record, now with the final apps outcome (analysis
    /// finding B-03). Raised on the context that started the switch.
    /// </summary>
    public event EventHandler<SwitchRecord>? AppsCompleted;

    public ObservableCollection<SwitchRecord> History { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    public partial bool IsSwitching { get; set; }

    /// <summary>Where the running switch goes, so the tray can name it ("Switching to Sim Rig …"); null while idle.</summary>
    [ObservableProperty]
    public partial Profile? SwitchingProfile { get; set; }

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
        Task apps = _orchestrator.CancelPendingAppsAsync();
        Task current = _current is { IsCompleted: false } running ? Task.WhenAll(running, apps) : apps;
        if (current.IsCompleted)
        {
            return true;
        }

        _log.Information("Waiting up to {Seconds} s for the running switch or apps to end", timeout.TotalSeconds);
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
        return CheckCoreAsync(AsSwitched(profile), rethrow: false, CancellationToken.None);
    }

    /// <summary>Where "back to the previous profile" goes right now: the hotkey, <c>toggle</c> and <c>rigshift://toggle</c> share it.</summary>
    public Profile? ToggleTarget =>
        ProfileEditing.ToggleTarget(_catalog.Profiles, _catalog.ActiveProfile?.Id, _catalog.PreviousProfileId, _settings.Current.DefaultProfileId);

    /// <summary>Command line: the caller needs the exception to report a failure instead of "busy".</summary>
    Task<SwitchResult?> IProfileSwitcher.SwitchAsync(Profile profile, SwitchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);
        return RunAsync(profile, request, rethrow: true, cancellationToken);
    }

    /// <summary>
    /// A dry run only reads: it takes neither the gate nor <see cref="IsSwitching"/>, so a hotkey during "Check" still
    /// switches (analysis finding B-13).
    /// </summary>
    private async Task<SwitchResult?> CheckCoreAsync(Profile profile, bool rethrow, CancellationToken cancellationToken)
    {
        if (_stopping.IsCancellationRequested)
        {
            return null;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        try
        {
            return await Task.Run(() => _orchestrator.CheckAsync(profile, linked.Token), CancellationToken.None);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            _log.Information("Check of {Profile} cancelled", profile.Name);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Check of {Profile} threw", profile.Name);
            if (rethrow)
            {
                throw;
            }

            return null;
        }
    }

    private Task<SwitchResult?> RunAsync(Profile profile, SwitchRequest request, bool rethrow, CancellationToken cancellationToken = default) =>
        OnUiThreadAsync(() => RunCoreAsync(profile, request, rethrow, cancellationToken));

    /// <summary>
    /// Runs <paramref name="work"/> on the UI thread and hands its result or exception back to the caller. A comparison
    /// of contexts would not do: WPF hands out a new context instance per dispatcher operation.
    /// </summary>
    private Task<T> OnUiThreadAsync<T>(Func<Task<T>> work)
    {
        if (_ui is null || Environment.CurrentManagedThreadId == _uiThread)
        {
            return work();
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.Post(_ => _ = RelayAsync(work, done), null);
        return done.Task;
    }

    private static async Task RelayAsync<T>(Func<Task<T>> work, TaskCompletionSource<T> done)
    {
        try
        {
            done.SetResult(await work());
        }
        catch (OperationCanceledException ex)
        {
            done.SetCanceled(ex.CancellationToken);
        }
        catch (Exception ex)
        {
            done.SetException(ex);
        }
    }

    /// <summary>The profile as it switches: without a Surround setting it means "off" once another profile uses Surround.</summary>
    private Profile AsSwitched(Profile profile) =>
        SurroundDefaults.Effective(profile, _catalog.Profiles) is var surround && surround != profile.Surround
            ? profile with { Surround = surround }
            : profile;

    private async Task<SwitchResult?> RunCoreAsync(Profile profile, SwitchRequest request, bool rethrow, CancellationToken cancellationToken)
    {
        profile = AsSwitched(profile);
        if (request.DryRun)
        {
            return await CheckCoreAsync(profile, rethrow, cancellationToken);
        }

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

        DateTimeOffset started = _time.GetLocalNow();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token, cancellationToken);
        try
        {
            // Inside the try on purpose: the setter runs foreign handlers, and one that throws used to leave the gate
            // taken for good – every later switch was refused as "another switch is running" until RigShift restarted.
            // The name first: a handler of IsSwitching already wants to say where the switch goes.
            SwitchingProfile = profile;
            IsSwitching = true;
            SwitchRequest effective = request with { DefaultConfirmTimeoutSeconds = _settings.Current.ConfirmTimeoutSeconds };
            Task<SwitchResult> running = Task.Run(() => _orchestrator.SwitchAsync(profile, effective, linked.Token), CancellationToken.None);
            _current = running;
            SwitchResult result = await running;

            RememberCatchUp(profile, result);
            SwitchRecord record = ToRecord(started, profile, result);
            await CompleteAsync(record);
            if (result.Apps == AppsOutcome.Pending)
            {
                _ = FollowAppsAsync(record, result.AppsCompletion);
            }

            await HealAsync(result);
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
            await CompleteAsync(new SwitchRecord(started, profile.Name, SwitchOutcome.Failed, AudioOutcome.NotConfigured, AppsOutcome.NotConfigured, 0,
                _time.GetLocalNow() - started, null, ex.Message, [], Note: ex is DisplayDriverHungException ? SwitchNote.DriverHung : SwitchNote.None));

            if (rethrow)
            {
                throw;
            }

            return null;
        }
        finally
        {
            IsSwitching = false;
            SwitchingProfile = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// Call after the active profile was refreshed on a display change. If the last switch skipped optional displays and
    /// its profile is still active, re-applies it once more of them are there. No time limit: a spacedesk viewer often
    /// connects minutes after the switch (deviation from the 60 s in PLAN 4.3, decided in M5).
    /// </summary>
    public Task CatchUpAsync() =>
        OnUiThreadAsync(async () =>
        {
            await CatchUpCoreAsync();
            return true;
        });

    private async Task CatchUpCoreAsync()
    {
        if (_pendingCatchUp is not { } pending)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            return;
        }

        DateTimeOffset started = _time.GetLocalNow();
        try
        {
            SwitchingProfile = pending.Profile;
            IsSwitching = true;

            // The active profile does not decide: when the missing display connects, Windows itself may restore whatever
            // layout its database holds for that set of monitors (M5 2026-09-13 20:17: spacedesk connected → Desk).
            Task<SwitchResult?> running = Task.Run(() => _orchestrator.CatchUpAsync(pending.Profile, pending.Displays, _stopping.Token), CancellationToken.None);
            _current = running;
            SwitchResult? result = await running;
            if (result is not null)
            {
                RememberCatchUp(pending.Profile, result);
                await CompleteAsync(ToRecord(started, pending.Profile, result));
                await HealAsync(result);
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
            SwitchingProfile = null;
            _gate.Release();
        }
    }

    /// <summary>
    /// Call after a display change, once the active profile was refreshed and a catch-up ran. A profile that became active
    /// without RigShift switching – Windows restored its layout – only has its displays; audio, apps and the rest are
    /// offered via <see cref="RestoredByWindows"/> (finding HW-15). Returns whether it was offered.
    /// </summary>
    public bool NoticeDisplayChange(Profile? before, Profile? after)
    {
        if (after is null || after.Id == before?.Id || IsSwitching || !HasMoreThanDisplays(after))
        {
            return false;
        }

        _log.Information("Windows restored the displays of {Profile} (before: {Before}); offering the rest", after.Name, before?.Name ?? "(none)");
        RestoredByWindows?.Invoke(this, after);
        return true;
    }

    internal static bool HasMoreThanDisplays(Profile profile) =>
        profile.Apps.Count > 0
        || profile.KeepAwake
        || profile.DisableCommunicationsDucking
        || profile.Audio is { Playback: not null } or { Recording: not null } or { PlaybackCommunications: not null } or { RecordingCommunications: not null }
            or { PlaybackVolumePercent: not null } or { RecordingVolumePercent: not null };

    private void RememberCatchUp(Profile profile, SwitchResult result) =>
        _pendingCatchUp = result.Outcome is SwitchOutcome.Applied or SwitchOutcome.AppliedPartially && result.Plan.ShouldRetryLater
            ? (profile, result.Plan.Resolved.Count)
            : result.Outcome == SwitchOutcome.Failed && _pendingCatchUp?.Profile.Id == profile.Id ? _pendingCatchUp
            : null;

    private SwitchRecord ToRecord(DateTimeOffset started, Profile profile, SwitchResult result) =>
        new(started, profile.Name, result.Outcome, result.Audio, result.Apps, result.Attempts, result.Duration, result.LastNativeError, result.Message,
            MissingForRecord(result).Select(m => SwitchMessages.NameOf(m.Assignment)).ToList(),
            profile.AppsWaitForUsbDeviceId is null && profile.AppsWaitForUsbDeviceName is null
                ? null
                : Core.Automation.UsbDeviceNames.NameOf(profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName, _settings.Current.UsbDeviceNames),
            Profile.AppsDeviceWaitSeconds, result.Note, Ambiguous: IsAmbiguous(result));

    private static bool IsAmbiguous(SwitchResult result) => result.Outcome == SwitchOutcome.Blocked && result.Plan.IsAmbiguous;

    /// <summary>
    /// A blocked switch names only the required displays that blocked it, not optional ones (finding HW-14) – and of those
    /// only the identical ones that could not be told apart, when that is why (K-03): the switch did not wait for the others.
    /// </summary>
    private static IEnumerable<MissingDisplay> MissingForRecord(SwitchResult result) =>
        IsAmbiguous(result) ? result.Plan.Missing.Where(m => !m.Assignment.IsOptional && m.Reason == MissingReason.Ambiguous)
        : result.Outcome == SwitchOutcome.Blocked && result.Plan.Missing.Any(m => !m.Assignment.IsOptional)
            ? result.Plan.Missing.Where(m => !m.Assignment.IsOptional)
            : result.Plan.Missing;

    /// <summary>
    /// Records the result and tells the listeners. Never throws: a listener that fails must not turn a switch that worked
    /// into a second, failed history entry (v4 finding A-15).
    /// </summary>
    private async Task CompleteAsync(SwitchRecord record)
    {
        try
        {
            History.Insert(0, record);
            while (History.Count > HistoryLength)
            {
                History.RemoveAt(History.Count - 1);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "A listener of the switch history threw for {Profile}", record.ProfileName);
        }

        // Awaited: whoever gets the result next (automation, tray) must see the profile that is active now (C-06, A-07).
        try
        {
            await _catalog.RefreshActiveAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "The active profile could not be refreshed after the switch to {Profile}", record.ProfileName);
        }

        // Displays that are only on in this profile (spacedesk) can list their rates now; the editor offers them later (HW-13).
        if (record.Outcome is SwitchOutcome.Applied or SwitchOutcome.AppliedPartially)
        {
            _ = _catalog.RememberActiveRefreshRatesAsync(CancellationToken.None);
        }

        foreach (EventHandler<SwitchRecord> listener in SwitchCompleted?.GetInvocationList().Cast<EventHandler<SwitchRecord>>() ?? [])
        {
            try
            {
                listener(this, record);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "A listener of the switch result threw for {Profile}; the result stands as {Outcome}", record.ProfileName, record.Outcome);
            }
        }
    }

    /// <summary>
    /// A switch that stayed tells where the monitors are now: monitors found on another port or graphics card, and serial
    /// numbers older profiles lack, go into every profile (v4 finding K-03). After the result, so nothing waits for the disk.
    /// </summary>
    private async Task HealAsync(SwitchResult result)
    {
        if (result.Outcome is not (SwitchOutcome.Applied or SwitchOutcome.AppliedPartially))
        {
            return;
        }

        try
        {
            await _catalog.HealIdentitiesAsync(result.Plan, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "The displays of {Profile} could not be written back into the profiles", result.Plan.Profile.Name);
        }
    }

    private async Task FollowAppsAsync(SwitchRecord record, Task<AppsOutcome> apps)
    {
        try
        {
            AppsOutcome outcome = await apps;
            SwitchRecord updated = record with { Apps = outcome };
            int index = History.IndexOf(record);
            if (index >= 0)
            {
                History[index] = updated;
            }

            AppsCompleted?.Invoke(this, updated);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Reporting the apps of {Profile} failed", record.ProfileName);
        }
    }
}
