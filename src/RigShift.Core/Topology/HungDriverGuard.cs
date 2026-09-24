using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// Puts a time limit on every call into the display driver (v4 finding K-07). A call that exceeds it counts as a hung
/// driver: until that call returns, every further one fails at once with <see cref="DisplayDriverHungException"/> instead of
/// queueing behind it in the kernel. A queued query without a limit used to hold the switch lock until RigShift restarted,
/// and every later switch was refused as "already running".
/// </summary>
public sealed class HungDriverGuard : IDisplayConfigurator
{
    private readonly IDisplayConfigurator _inner;
    private readonly SwitchOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger _log;

    /// <summary>The call that did not return in time; the driver counts as hung while it runs.</summary>
    private Task? _stuck;

    public HungDriverGuard(IDisplayConfigurator inner, SwitchOptions options, TimeProvider time, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(log);
        _inner = inner;
        _options = options;
        _time = time;
        _log = log.ForContext<HungDriverGuard>();
    }

    /// <summary>A call is stuck in the driver right now.</summary>
    public bool IsHung => Volatile.Read(ref _stuck) is { IsCompleted: false };

    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken) =>
        CallAsync(() => _inner.QueryAsync(cancellationToken), _options.QueryCallTimeout, "query", cancellationToken);

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken) =>
        CallAsync(() => _inner.ApplyAsync(plan, options, cancellationToken), _options.ApplyCallTimeout, "display change", cancellationToken);

    public Task<int> SetHdrAsync(AttachedDisplay display, bool enabled, CancellationToken cancellationToken) =>
        CallAsync(() => _inner.SetHdrAsync(display, enabled, cancellationToken), _options.HdrCallTimeout, "HDR change", cancellationToken);

    public Task<IReadOnlyList<RefreshRate>> ListRefreshRatesAsync(DisplayIdentity identity, int width, int height, CancellationToken cancellationToken) =>
        CallAsync(() => _inner.ListRefreshRatesAsync(identity, width, height, cancellationToken), _options.QueryCallTimeout, "refresh rate query", cancellationToken);

    private async Task<T> CallAsync<T>(Func<Task<T>> call, TimeSpan limit, string what, CancellationToken cancellationToken)
    {
        if (IsHung)
        {
            _log.Warning("A {Call} was refused: an earlier call is still stuck in the graphics driver", what);
            throw new DisplayDriverHungException();
        }

        Task<T> running = call();
        try
        {
            return await running.WaitAsync(limit, _time, cancellationToken);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _stuck, running);
            _log.Error("The graphics driver did not answer a {Call} within {Limit}; display calls fail at once until it does", what, limit);
            _ = running.ContinueWith(
                done => _log.Warning("The graphics driver answered the {Call} after all ({Status}); display calls work again", what, done.Status),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            throw new DisplayDriverHungException();
        }
    }
}
