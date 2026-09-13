using RigShift.Core.Abstractions;
using RigShift.Core.Topology;

namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// Scripted display boundary: returns snapshots in order (the last one repeats) and native error codes
/// per apply call in order (0 once the script is exhausted).
/// </summary>
internal sealed class FakeDisplayConfigurator : IDisplayConfigurator
{
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

    public Task<DisplaySnapshot> QueryAsync(CancellationToken cancellationToken)
    {
        QueryCount++;
        if (_snapshots.Count > 0)
        {
            _current = _snapshots.Dequeue();
        }

        return Task.FromResult(_current);
    }

    public Task<int> ApplyAsync(TopologyPlan plan, ApplyOptions options, CancellationToken cancellationToken)
    {
        Applied.Add((plan, options));
        return Task.FromResult(_applyResults.Count > 0 ? _applyResults.Dequeue() : 0);
    }
}
