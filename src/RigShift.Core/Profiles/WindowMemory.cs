using RigShift.Core.Abstractions;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Core.Profiles;

/// <summary>
/// Remembers where every window sat when a profile was left and puts them back when it returns
/// (<see cref="Profile.RememberWindows"/>). Unlike a game's <see cref="WindowLayout"/> nobody captures anything: the
/// desk with its dozen windows is whatever it was a moment before the switch.
///
/// Windows are found again by their handle first – that is what tells five windows of one program apart – and by
/// program and title when the handle is gone (the program was restarted in between).
/// </summary>
public sealed class WindowMemory(IWindowLayout windows, IWindowMemoryStore store, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IWindowLayout _windows = windows;
    private readonly IWindowMemoryStore _store = store;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log.ForContext<WindowMemory>();

    /// <summary>
    /// The open windows, taken before a switch changes anything – afterwards Windows has already moved them. Returns
    /// <c>null</c> when the profile being left does not remember its windows, is unknown, or is the target itself.
    /// </summary>
    public RememberedWindows? Capture(Profile? leaving, Profile target)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (leaving is not { RememberWindows: true } || leaving.Id == target.Id)
        {
            return null;
        }

        try
        {
            long started = _time.GetTimestamp();
            IReadOnlyList<OpenWindow> open = _windows.Open();
            _log.Information("Read {Count} open window(s) of {Profile} in {Milliseconds:0} ms",
                open.Count, leaving.Name, _time.GetElapsedTime(started).TotalMilliseconds);
            return new RememberedWindows
            {
                Windows = [.. open.Select(w => new RememberedWindow
                {
                    Handle = w.Handle,
                    Placement = new WindowPlacement { ProcessName = w.ProcessName, Title = w.Title, Bounds = w.Bounds, State = w.State },
                })],
                CapturedAt = _time.GetUtcNow(),
            };
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "The open windows of {Profile} could not be read, they are not remembered", leaving.Name);
            return null;
        }
    }

    /// <summary>Keeps what <see cref="Capture"/> read, once the switch away from <paramref name="leaving"/> stays.</summary>
    public async Task RememberAsync(Profile leaving, RememberedWindows captured, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(leaving);
        ArgumentNullException.ThrowIfNull(captured);

        try
        {
            await _store.SaveAsync(leaving.Id, captured, cancellationToken);
            _log.Information("Remembered {Count} window(s) of {Profile}", captured.Windows.Count, leaving.Name);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "The windows of {Profile} could not be remembered", leaving.Name);
        }
    }

    /// <summary>
    /// Puts the remembered windows of <paramref name="profile"/> back. Looks again after a moment and repeats while
    /// something moved: Windows rearranges windows itself after a display change, and a program that crosses to a
    /// display with another scaling resizes itself on the way. Returns <c>null</c> when there is nothing to do.
    /// </summary>
    public async Task<WindowLayoutResult?> RestoreAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!profile.RememberWindows)
        {
            return null;
        }

        RememberedWindows? remembered = await _store.LoadAsync(profile.Id, cancellationToken);
        if (remembered is not { Windows.Count: > 0 })
        {
            _log.Information("No windows remembered for {Profile} yet", profile.Name);
            return null;
        }

        // Paired once: the fallback by program and title depends on the stacking order, which placing windows changes.
        // Bottom-most first, so a maximized window – which cannot be shown without activating it – ends up on top only
        // if it was on top.
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.MatchRemembered(remembered.Windows, _windows.Open());
        List<WindowMatch> pending = [.. matches.Where(m => m.Window is not null).Reverse()];
        int missing = matches.Count - pending.Count;
        int refused = 0;
        var placed = new HashSet<nint>();

        for (int attempt = 1; attempt <= _options.WindowRestoreAttempts && pending.Count > 0; attempt++)
        {
            Dictionary<nint, OpenWindow> now;
            if (attempt == 1)
            {
                now = pending.ToDictionary(m => m.Window!.Handle, m => m.Window!);
            }
            else
            {
                await Task.Delay(_options.WindowRestoreDelay, _time, cancellationToken);
                now = [];
                foreach (OpenWindow window in _windows.Open())
                {
                    now[window.Handle] = window;
                }
            }

            int moved = 0;
            foreach (WindowMatch match in pending.ToArray())
            {
                nint handle = match.Window!.Handle;
                if (!now.TryGetValue(handle, out OpenWindow? current))
                {
                    continue;
                }

                if (current.Bounds == match.Placement.Bounds && current.State == match.Placement.State)
                {
                    continue;
                }

                if (_windows.Place(handle, match.Placement.Bounds, match.Placement.State))
                {
                    placed.Add(handle);
                    moved++;
                }
                else
                {
                    // Elevated, hung or on a display that is off: asking again changes nothing.
                    refused++;
                    pending.Remove(match);
                }
            }

            if (moved == 0)
            {
                break;
            }

            if (attempt > 1)
            {
                _log.Information("{Count} window(s) of {Profile} had moved again, put back (attempt {Attempt})", moved, profile.Name, attempt);
            }
        }

        _log.Information("Windows of {Profile}: {Placed} put back, {Refused} refused, {Missing} no longer open, {Total} remembered",
            profile.Name, placed.Count, refused, missing, matches.Count);
        return new WindowLayoutResult(placed.Count, refused, missing);
    }
}

/// <summary>The windows of one profile as they were when it was left.</summary>
public sealed record RememberedWindows
{
    /// <summary>Topmost first, as Windows stacks them.</summary>
    public IReadOnlyList<RememberedWindow> Windows { get; init; } = [];

    public DateTimeOffset CapturedAt { get; init; }
}

/// <summary>One remembered window.</summary>
public sealed record RememberedWindow
{
    /// <summary>
    /// The window's handle when it was read. Good for as long as the window lives, also across a restart of RigShift;
    /// after that it may name another window, so it only counts together with the program's name.
    /// </summary>
    public long Handle { get; init; }

    public required WindowPlacement Placement { get; init; }
}
