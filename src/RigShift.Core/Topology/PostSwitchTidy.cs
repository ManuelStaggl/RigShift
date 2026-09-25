using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Tidies up after an arrangement that stays: windows left on displays that are off, and the profile's desktop symbols.
/// Neither ever fails a switch – an untidy desktop is a log line. Both run after the result, in the background: they wait
/// for Windows to finish its own rearranging first, and that used to hold up the result, the hotkeys and the next switch
/// by one to eleven seconds (v4 finding K-04). Logs under the orchestrator's context, so the log source stays the same.
/// </summary>
internal sealed class PostSwitchTidy(IWindowRescuer windows, IDesktopIcons desktopIcons, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IWindowRescuer _windows = windows;
    private readonly IDesktopIcons _desktopIcons = desktopIcons;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;

    private readonly Lock _gate = new();

    /// <summary>The tidy-up after the last result, running until done or cancelled.</summary>
    private Pending? _pending;

    /// <summary>
    /// Starts the tidy-up in the background: the windows, and with a <paramref name="profile"/> its desktop symbols, side by
    /// side. Ends the tidy-up of an earlier result first. Completes with what became of the symbols (K-15); never faults.
    /// </summary>
    public Task<DesktopIconOutcome> Start(Profile? profile)
    {
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task<DesktopIconOutcome> run = Task.Run(() => RunAsync(profile, token), CancellationToken.None);
        Pending? previous;
        lock (_gate)
        {
            previous = _pending;
            _pending = new Pending(run, cancellation);
        }

        if (previous is not null)
        {
            _ = EndAsync(previous);
        }

        return run;
    }

    /// <summary>
    /// Cancels the tidy-up of an earlier result and completes once it ended. The cancellation is requested before this
    /// method first yields: a new switch must not have its windows moved by the old one.
    /// </summary>
    public Task CancelPendingAsync()
    {
        Pending? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }

        return pending is null ? Task.CompletedTask : EndAsync(pending);
    }

    private static async Task EndAsync(Pending pending)
    {
        try
        {
            if (!pending.Run.IsCompleted)
            {
                await pending.Cancellation.CancelAsync();
            }

            await pending.Run;
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    private async Task<DesktopIconOutcome> RunAsync(Profile? profile, CancellationToken cancellationToken)
    {
        Task rescue = RescueWindowsAsync(cancellationToken);
        Task<DesktopIconOutcome> icons = profile is null ? SwitchResult.NothingToTidy : RestoreDesktopIconsAsync(profile, cancellationToken);
        try
        {
            await Task.WhenAll(rescue, icons);
            return await icons;
        }
        catch (OperationCanceledException)
        {
            _log.Information("Tidy-up after the switch cancelled");
            return DesktopIconOutcome.NotConfigured;
        }
    }

    /// <summary>
    /// After every apply that stays (switch, restore after a failure, catch-up): windows left on a display that is off
    /// now move to the primary display. Waits a moment first, because right after the arrangement changed Windows moves
    /// them itself.
    /// </summary>
    private async Task RescueWindowsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_options.WindowRescueDelay, _time, cancellationToken);
            long started = _time.GetTimestamp();
            int moved = _windows.RescueOffscreenWindows();
            _log.Information("Moved {Count} window(s) from displays that are off to the primary display in {Milliseconds:0} ms",
                moved, _time.GetElapsedTime(started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Moving windows from displays that are off failed");
        }
    }

    /// <summary>
    /// Puts the desktop symbols back where this profile wants them – only once the switch is staying, because a rejected
    /// switch must not leave the desktop rearranged.
    ///
    /// Explorer lays the symbols out itself when the arrangement changes, and it does so a moment after the change – so
    /// one attempt can be undone again right after it. Each pass therefore checks whether what it placed is still in
    /// place and repeats while something moved.
    /// </summary>
    private async Task<DesktopIconOutcome> RestoreDesktopIconsAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.DesktopIcons is not { IsEmpty: false } wanted)
        {
            return DesktopIconOutcome.NotConfigured;
        }

        try
        {
            for (int attempt = 1; attempt <= _options.DesktopIconAttempts; attempt++)
            {
                await Task.Delay(_options.DesktopIconDelay, _time, cancellationToken);
                DesktopIconResult result = _desktopIcons.Restore(wanted);
                if (result.Outcome != DesktopIconOutcome.Restored)
                {
                    _log.Information("Desktop symbols for {Profile}: {Outcome}", profile.Name, result.Outcome);
                    return result.Outcome;
                }

                _log.Information("Desktop symbols for {Profile}: {Placed} placed, {Missing} gone (attempt {Attempt})",
                    profile.Name, result.Placed, result.Missing, attempt);

                // What the shell actually made of it – with "align to grid" it snaps to the nearest cell, so comparing
                // against the wish would never agree. The next pass only has to notice that Explorer moved them again.
                DesktopIconLayout? settled = _desktopIcons.Capture();
                if (settled is null || attempt == _options.DesktopIconAttempts)
                {
                    return DesktopIconOutcome.Restored;
                }

                await Task.Delay(_options.DesktopIconDelay, _time, cancellationToken);
                if (!Moved(settled, _desktopIcons.Capture()))
                {
                    return DesktopIconOutcome.Restored;
                }

                _log.Information("Desktop symbols for {Profile} moved again, putting them back once more", profile.Name);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the desktop symbols failed");
            return DesktopIconOutcome.Unavailable;
        }

        return DesktopIconOutcome.Restored;
    }

    /// <summary>Whether any symbol sits somewhere else than it did in <paramref name="settled"/>.</summary>
    private static bool Moved(DesktopIconLayout settled, DesktopIconLayout? now)
    {
        if (now is null)
        {
            return false;
        }

        Dictionary<string, DesktopIcon> current = new(StringComparer.OrdinalIgnoreCase);
        foreach (DesktopIcon icon in now.Icons)
        {
            current[icon.Item] = icon;
        }

        return settled.Icons.Any(icon => current.TryGetValue(icon.Item, out DesktopIcon? at) && (at.X != icon.X || at.Y != icon.Y));
    }

    private sealed record Pending(Task<DesktopIconOutcome> Run, CancellationTokenSource Cancellation);
}
