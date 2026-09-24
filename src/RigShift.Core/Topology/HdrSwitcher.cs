using System.Globalization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// HDR per display, right after an arrangement was applied. Logs under the orchestrator's context, so the log source
/// stays the same.
/// </summary>
internal sealed class HdrSwitcher(IDisplayConfigurator display, SwitchOptions options, TimeProvider time, ILogger log)
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);

    private readonly IDisplayConfigurator _display = display;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;

    /// <summary>
    /// Works on a fresh snapshot: a display that was just switched on only reports its HDR state once active. Rollbacks
    /// restore it the same way, because the previous topology carries the state it had. Failures are logged and never
    /// fail the switch.
    /// </summary>
    public async Task SwitchAsync(Profile profile, CancellationToken cancellationToken)
    {
        List<DisplayAssignment> wantedDisplays = [.. profile.Displays.Where(d => d.Hdr is not null)];
        if (wantedDisplays.Count == 0)
        {
            return;
        }

        long started = _time.GetTimestamp();
        try
        {
            DisplaySnapshot now = await _display.QueryAsync(cancellationToken);
            if (!NeedsCheck(wantedDisplays, now))
            {
                _log.Information("HDR for {Profile} is already as wanted", profile.Name);
                return;
            }

            // A monitor may still do its handshake right after the apply and drop off the bus; switching HDR then is what
            // froze the test PC (finding HW-12). Two snapshots in a row must agree first.
            if (await WaitForSettledDisplaysAsync(now, cancellationToken) is not { } settled)
            {
                _log.Warning("HDR for {Profile} left unchanged: the displays did not settle within {Budget}", profile.Name, _options.HdrSettleBudget);
                return;
            }

            Pass pass = await SetAsync(wantedDisplays, settled, cancellationToken);
            if (!pass.TimedOut && pass.Unknown.Count > 0)
            {
                // Right after an apply the driver may not report HDR yet (analysis finding B-12): ask once more.
                _log.Information("HDR state of {Count} display(s) not reported yet, asking again in {Delay}", pass.Unknown.Count, RetryDelay);
                await Task.Delay(RetryDelay, _time, cancellationToken);
                now = await _display.QueryAsync(cancellationToken);
                foreach (DisplayAssignment wanted in (await SetAsync(pass.Unknown, now, cancellationToken)).Unknown)
                {
                    _log.Warning("Display {Display} does not support HDR, left unchanged", DisplayNames.Of(wanted));
                }
            }

            _log.Information("HDR for {Profile} handled in {Milliseconds:0} ms", profile.Name, _time.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "HDR for {Profile} could not be set", profile.Name);
        }
    }

    /// <summary>Whether an active display of the profile reports another HDR state than wanted, or none yet.</summary>
    private static bool NeedsCheck(IReadOnlyList<DisplayAssignment> wantedDisplays, DisplaySnapshot now) =>
        wantedDisplays.Any(wanted => now.FindActive(wanted.Identity)?.ActiveMode is { } mode && mode.Hdr != wanted.Hdr);

    /// <summary>
    /// Re-queries until two snapshots in a row show the same displays with the same modes (HDR state aside), at most
    /// <see cref="SwitchOptions.HdrSettleBudget"/>. Returns the settled snapshot, or <c>null</c> when they kept changing.
    /// </summary>
    private async Task<DisplaySnapshot?> WaitForSettledDisplaysAsync(DisplaySnapshot first, CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + _options.HdrSettleBudget;
        DisplaySnapshot previous = first;
        while (true)
        {
            await Task.Delay(_options.PollInterval, _time, cancellationToken);
            DisplaySnapshot current = await _display.QueryAsync(cancellationToken);
            if (LayoutKey(current).SetEquals(LayoutKey(previous)))
            {
                return current;
            }

            if (_time.GetUtcNow() >= deadline)
            {
                return null;
            }

            _log.Debug("Displays still changing after the apply, waiting before HDR");
            previous = current;
        }
    }

    private static HashSet<string> LayoutKey(DisplaySnapshot snapshot) =>
        snapshot.Displays
            .Select(d => d.ActiveMode is { } m
                ? string.Create(CultureInfo.InvariantCulture,
                    $"{d.Identity.TargetDevicePath}|{d.IsAvailable}|{m.Width}x{m.Height}@{m.RefreshNumerator}/{m.RefreshDenominator}|{m.PositionX},{m.PositionY}")
                : string.Create(CultureInfo.InvariantCulture, $"{d.Identity.TargetDevicePath}|{d.IsAvailable}|off"))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sets HDR where the state differs. Returns the active displays that reported no HDR state, and whether a call did not
    /// return within <see cref="SwitchOptions.HdrCallTimeout"/> – then the remaining displays are left alone.
    /// </summary>
    private async Task<Pass> SetAsync(IReadOnlyList<DisplayAssignment> wantedDisplays, DisplaySnapshot now, CancellationToken cancellationToken)
    {
        var unknown = new List<DisplayAssignment>();
        foreach (DisplayAssignment wanted in wantedDisplays)
        {
            AttachedDisplay? target = now.FindActive(wanted.Identity);
            if (wanted.Hdr is not { } enabled || target?.ActiveMode is not { } mode)
            {
                continue;
            }

            if (mode.Hdr is null)
            {
                unknown.Add(wanted);
            }
            else if (mode.Hdr != enabled)
            {
                int code;
                try
                {
                    // The time limit is the driver guard's (K-07).
                    code = await _display.SetHdrAsync(target, enabled, cancellationToken);
                }
                catch (DisplayDriverHungException)
                {
                    _log.Error("HDR of {Display} did not return within {Timeout}; the graphics driver may hang. HDR is left alone for the rest of this switch",
                        DisplayNames.Of(wanted), _options.HdrCallTimeout);
                    return new Pass(unknown, TimedOut: true);
                }

                if (code != 0)
                {
                    _log.Warning("HDR of {Display} could not be set to {Enabled} (native error {Error})", DisplayNames.Of(wanted), enabled, code);
                }
            }
        }

        return new Pass(unknown, TimedOut: false);
    }

    private sealed record Pass(List<DisplayAssignment> Unknown, bool TimedOut);
}
