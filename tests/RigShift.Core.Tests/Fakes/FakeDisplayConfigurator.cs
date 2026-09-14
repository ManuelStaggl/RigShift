using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// Scripted display boundary: returns snapshots in order (the last one repeats) and native error codes
/// per apply call in order (0 once the script is exhausted). Exceptions can be scripted per call as well.
/// </summary>
internal sealed class FakeDisplayConfigurator : IDisplayConfigurator
{
    private readonly Lock _gate = new();
    private readonly Queue<DisplaySnapshot> _snapshots;
    private readonly Queue<int> _applyResults;
    private DisplaySnapshot _current;

    public FakeDisplayConfigurator(IEnumerable<DisplaySnapshot> snapshots, IEnumerable<int>? applyResults = null)
    {
        _snapshots = new Queue<DisplaySnapshot>(snapshots);
        _current = _snapshots.Peek();
        _applyResults = new Queue<int>(applyResults ?? []);
    }

    public FakeDisplayConfigurator(DisplaySnapshot snapshot, IEnumerable<int>? applyResults = null)
        : this([snapshot], applyResults)
    {
    }

    public int QueryCount { get; private set; }

    public List<(TopologyPlan Plan, ApplyOptions Options)> Applied { get; } = [];

    /// <summary>Per apply call in order: an exception to throw instead of applying, or <c>null</c> to apply normally.</summary>
    public Queue<Exception?> ApplyExceptions { get; } = new();

    /// <summary>Per query in order: an exception to throw instead of answering, or <c>null</c> to answer normally.</summary>
    public Queue<Exception?> QueryExceptions { get; } = new();

    public List<(string TargetDevicePath, bool Enabled)> HdrSet { get; } = [];

    /// <summary>Native result of setting HDR on any display without an entry in <see cref="HdrResults"/>.</summary>
    public int HdrResult { get; set; }

    /// <summary>Native result of setting HDR per target device path.</summary>
    public Dictionary<string, int> HdrResults { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<RefreshRate> RefreshRates { get; } = [];

    /// <summary>From now on every query returns <paramref name="snapshot"/>, e.g. after a display connected.</summary>
    public void SetSnapshot(DisplaySnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshots.Clear();
            _current = snapshot;
        }
    }

    public void EnqueueApplyResults(params int[] results)
    {
        lock (_gate)
        {
            foreach (int result in results)
            {
                _applyResults.Enqueue(result);
            }
        }
    }

    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            QueryCount++;
            if (QueryExceptions.Count > 0 && QueryExceptions.Dequeue() is { } exception)
            {
                throw exception;
            }

            if (_snapshots.Count > 0)
            {
                _current = _snapshots.Dequeue();
            }

            return Task.FromResult(_current);
        }
    }

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Applied.Add((plan, options));
            if (ApplyExceptions.Count > 0 && ApplyExceptions.Dequeue() is { } exception)
            {
                throw exception;
            }

            return Task.FromResult(_applyResults.Count > 0 ? _applyResults.Dequeue() : 0);
        }
    }

    public Task<int> SetHdrAsync(AttachedDisplay display, bool enabled, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            HdrSet.Add((display.Identity.TargetDevicePath, enabled));
            return Task.FromResult(HdrResults.GetValueOrDefault(display.Identity.TargetDevicePath, HdrResult));
        }
    }

    public Task<IReadOnlyList<RefreshRate>> ListRefreshRatesAsync(DisplayIdentity identity, int width, int height, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RefreshRate>>(RefreshRates.ToList());
}
