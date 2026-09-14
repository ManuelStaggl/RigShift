using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The apps part of a switch: waits for the profile's USB device, then starts and ends apps after the switch result.
/// Logs under the orchestrator's context, so the log source stays the same.
/// </summary>
internal sealed class AppRunner(IAppLauncher apps, IUsbDeviceList usbDevices, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IAppLauncher _apps = apps;
    private readonly IUsbDeviceList _usbDevices = usbDevices;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;

    private readonly Lock _appsLock = new();

    /// <summary>The apps of the last switch, running after its result until done or cancelled (analysis finding B-03).</summary>
    private PendingApps? _pendingApps;

    /// <summary>
    /// Cancels the apps of an earlier switch that still run or wait for their device, and completes once they ended.
    /// The cancellation is requested before this method first yields.
    /// </summary>
    public Task CancelPendingAsync()
    {
        PendingApps? pending;
        lock (_appsLock)
        {
            pending = _pendingApps;
            _pendingApps = null;
        }

        return pending is null ? Task.CompletedTask : EndAppsAsync(pending);
    }

    private async Task EndAppsAsync(PendingApps pending)
    {
        try
        {
            if (!pending.Run.IsCompleted)
            {
                _log.Information("Cancelling the apps of {Profile}", pending.ProfileName);
                await pending.Cancellation.CancelAsync();
            }

            await pending.Run;
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    public Task<AppsOutcome> Start(Profile profile)
    {
        if (profile.Apps.Count == 0)
        {
            return SwitchResult.NoApps;
        }

        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task<AppsOutcome> run = Task.Run(() => RunAppsAsync(profile, token), CancellationToken.None);
        PendingApps? previous;
        lock (_appsLock)
        {
            previous = _pendingApps;
            _pendingApps = new PendingApps(profile.Name, run, cancellation);
        }

        if (previous is not null)
        {
            _ = EndAppsAsync(previous);
        }

        return run;
    }

    /// <summary>Runs after the switch result on its own cancellation; never throws.</summary>
    private async Task<AppsOutcome> RunAppsAsync(Profile profile, CancellationToken cancellationToken)
    {
        long startedAt = _time.GetTimestamp();
        try
        {
            // Wheel software and games want to see the device when they start; without it they start anyway.
            bool deviceMissing = !await WaitForAppsDeviceAsync(profile, cancellationToken);
            AppsOutcome started = await RunAppActionsAsync(profile.Apps, cancellationToken);
            AppsOutcome outcome = deviceMissing ? AppsOutcome.DeviceMissing : started;
            _log.Information("Apps for {Profile}: {Apps} after {Seconds:0.0} s", profile.Name, outcome, _time.GetElapsedTime(startedAt).TotalSeconds);
            return outcome;
        }
        catch (OperationCanceledException)
        {
            _log.Information("Apps for {Profile} cancelled after {Seconds:0.0} s", profile.Name, _time.GetElapsedTime(startedAt).TotalSeconds);
            return AppsOutcome.Cancelled;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Apps for {Profile} failed", profile.Name);
            return AppsOutcome.Incomplete;
        }
    }

    private async Task<AppsOutcome> RunAppActionsAsync(IReadOnlyList<AppAction> apps, CancellationToken cancellationToken)
    {
        bool complete = true;
        foreach (AppAction app in apps)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                bool running = _apps.IsRunning(app.Path);
                if (app.Kind == AppActionKind.Start && running)
                {
                    _log.Information("App {App} already runs, not started", app.Path);
                    continue;
                }

                if (app.Kind == AppActionKind.Stop && !running)
                {
                    _log.Information("App {App} does not run, nothing to end", app.Path);
                    continue;
                }

                if (app.Kind == AppActionKind.Start)
                {
                    _apps.Start(app.Path, app.Arguments);
                    _log.Information("App {App} started", app.Path);
                }
                else if (await _apps.StopAsync(app.Path, _options.AppStopGrace, cancellationToken))
                {
                    _log.Information("App {App} ended", app.Path);
                }
                else
                {
                    _log.Warning("App {App} could not be ended", app.Path);
                    complete = false;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Warning(ex, "{Action} of app {App} failed", app.Kind, app.Path);
                complete = false;
                continue;
            }

            if (app.WaitSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(app.WaitSeconds), _time, cancellationToken);
            }
        }

        return complete ? AppsOutcome.Applied : AppsOutcome.Incomplete;
    }

    /// <summary>
    /// Polls for the device the profile's apps wait for, up to its wait time. True when it is there or none is set.
    /// </summary>
    private async Task<bool> WaitForAppsDeviceAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId) is not { } deviceId)
        {
            return true;
        }

        const int seconds = Profile.AppsDeviceWaitSeconds;
        string name = profile.AppsWaitForUsbDeviceName ?? deviceId;
        DateTimeOffset deadline = _time.GetUtcNow() + TimeSpan.FromSeconds(seconds);
        long started = _time.GetTimestamp();
        bool waited = false;

        while (!IsUsbDevicePresent(deviceId))
        {
            if (_time.GetUtcNow() >= deadline)
            {
                _log.Warning("Device {Device} did not show up within {Seconds} s, starting apps anyway", name, seconds);
                return false;
            }

            if (!waited)
            {
                _log.Information("Waiting up to {Seconds} s for device {Device} before starting apps", seconds, name);
                waited = true;
            }

            await Task.Delay(_options.DevicePollInterval, _time, cancellationToken);
        }

        if (waited)
        {
            _log.Information("Device {Device} showed up after {Elapsed:0.0} s", name, _time.GetElapsedTime(started).TotalSeconds);
        }

        return true;
    }

    private bool IsUsbDevicePresent(string deviceId)
    {
        try
        {
            return _usbDevices.PresentDeviceIds().Contains(deviceId);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed while waiting for {Device}", deviceId);
            return false;
        }
    }

    private sealed record PendingApps(string ProfileName, Task<AppsOutcome> Run, CancellationTokenSource Cancellation);
}
