using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Tidies up after an arrangement that stays: lost windows, remembered windows and desktop symbols. Runs after the
/// result, in the background, and never fails a switch.
/// </summary>
internal sealed class PostSwitchTidy(
    IWindowRescuer windows, IDesktopIcons desktopIcons, WindowMemory? windowMemory, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IWindowRescuer _windows = windows;
    private readonly WindowMemory? _windowMemory = windowMemory;
    private readonly IDesktopIcons _desktopIcons = desktopIcons;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;

    private readonly Lock _gate = new();

    private Pending? _pending;

    /// <summary>
    /// Starts the windows and, with a profile, its desktop symbols in the background, after ending an earlier tidy-up.
    /// Never faults.
    /// </summary>
    public Task<DesktopIconOutcome> Start(Profile? profile, bool restoreWindows = false)
    {
        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task<DesktopIconOutcome> run = Task.Run(() => RunAsync(profile, restoreWindows, token), CancellationToken.None);
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
    /// The cancellation is requested before the first yield: a new switch must not have its windows moved by the old
    /// one.
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

    private async Task<DesktopIconOutcome> RunAsync(Profile? profile, bool restoreWindows, CancellationToken cancellationToken)
    {
        Task rescue = TidyWindowsAsync(restoreWindows ? profile : null, cancellationToken);
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

    // Lost windows first, then the remembered ones; the others stay where the rescue put them.
    private async Task TidyWindowsAsync(Profile? remembering, CancellationToken cancellationToken)
    {
        await RescueWindowsAsync(cancellationToken);
        if (remembering is null || _windowMemory is null)
        {
            return;
        }

        try
        {
            await _windowMemory.RestoreAsync(remembering, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Putting the remembered windows of {Profile} back failed", remembering.Name);
        }
    }

    // Waits a moment first: right after the arrangement changed, Windows moves the windows itself.
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

    // Explorer lays the symbols out again a moment after the change, so each pass checks and repeats while something
    // moved.
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
