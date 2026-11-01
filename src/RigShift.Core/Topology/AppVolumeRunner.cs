using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The profile's app volumes, after a switch that stays. A program often opens its sound only later – a voice chat
/// joining a call, a player starting – so the ones not found are looked for again until <see cref="SwitchOptions.AppVolumeWait"/>.
/// Windows remembers a program's volume, so one hit per program is enough.
/// </summary>
internal sealed class AppVolumeRunner(IAppVolumes volumes, SwitchOptions options, TimeProvider time, ILogger log)
{
    private readonly IAppVolumes _volumes = volumes;
    private readonly SwitchOptions _options = options;
    private readonly TimeProvider _time = time;
    private readonly ILogger _log = log;
    private readonly Lock _lock = new();
    private Pending? _pending;

    /// <summary>Completes with the programs that never had sound open within the wait; never faults.</summary>
    public Task<IReadOnlyList<string>> Start(Profile profile)
    {
        if (profile.AppVolumes.Count == 0)
        {
            return SwitchResult.NoAppsMissing;
        }

        var cancellation = new CancellationTokenSource();
        CancellationToken token = cancellation.Token;
        Task<IReadOnlyList<string>> run = Task.Run(() => RunAsync(profile, token), CancellationToken.None);
        Pending? previous;
        lock (_lock)
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

    public Task CancelPendingAsync()
    {
        Pending? pending;
        lock (_lock)
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
            await pending.Cancellation.CancelAsync();
            await pending.Run;
        }
        finally
        {
            pending.Cancellation.Dispose();
        }
    }

    private async Task<IReadOnlyList<string>> RunAsync(Profile profile, CancellationToken cancellationToken)
    {
        List<AppVolume> waiting = [.. profile.AppVolumes];
        DateTimeOffset deadline = _time.GetUtcNow() + _options.AppVolumeWait;
        try
        {
            while (true)
            {
                waiting.RemoveAll(v => _volumes.Adjust(v.Executable, v.VolumePercent, v.Muted) > 0);
                if (waiting.Count == 0 || _time.GetUtcNow() >= deadline)
                {
                    break;
                }

                await Task.Delay(_options.AppVolumePollInterval, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Information("App volumes of {Profile} cancelled", profile.Name);
            return [];
        }

        if (waiting.Count > 0)
        {
            _log.Warning("App volumes of {Profile}: no sound from {Apps} within {Wait}", profile.Name, waiting.Select(v => v.Executable), _options.AppVolumeWait);
        }

        return [.. waiting.Select(v => v.Executable)];
    }

    private sealed record Pending(Task<IReadOnlyList<string>> Run, CancellationTokenSource Cancellation);
}
