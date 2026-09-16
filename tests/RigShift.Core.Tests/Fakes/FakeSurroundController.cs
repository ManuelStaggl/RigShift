using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Core.Tests.Fakes;

/// <summary>
/// A Surround controller in a field. It behaves like the real one where that matters for the orchestrator: a state
/// that already matches answers <see cref="SurroundOutcome.Unchanged"/>, and the applied settings are kept in order so
/// a test can see that the rollback put the old one back.
/// </summary>
internal sealed class FakeSurroundController : ISurroundController
{
    /// <summary>What the driver reports. Null stands for "Surround is off".</summary>
    public SurroundGrid? ActiveGrid { get; set; }

    public SurroundAvailability Availability { get; set; } = SurroundAvailability.Available;

    /// <summary>Answer for the next apply; the state is then not changed. Null lets the apply succeed.</summary>
    public SurroundApplyResult? NextFailure { get; set; }

    /// <summary>Every setting that was applied, in order.</summary>
    public List<SurroundSetting> Applied { get; } = [];

    public int Queries { get; private set; }

    public Task<SurroundState> QueryAsync(CancellationToken cancellationToken)
    {
        Queries++;
        if (Availability != SurroundAvailability.Available)
        {
            return Task.FromResult(SurroundState.Unavailable(Availability, "fake"));
        }

        return Task.FromResult(new SurroundState
        {
            Availability = SurroundAvailability.Available,
            Grids = ActiveGrid is null ? [] : [ActiveGrid],
        });
    }

    public Task<SurroundApplyResult> ApplyAsync(SurroundSetting wanted, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wanted);

        Applied.Add(wanted);
        if (NextFailure is { } failure)
        {
            NextFailure = null;
            return Task.FromResult(failure);
        }

        if (Availability != SurroundAvailability.Available)
        {
            return Task.FromResult(new SurroundApplyResult { Outcome = SurroundOutcome.NotAvailable, Message = "fake" });
        }

        SurroundGrid? wantedGrid = wanted.Enabled ? wanted.Grid : null;
        if (Equal(ActiveGrid, wantedGrid))
        {
            return Task.FromResult(SurroundApplyResult.Unchanged);
        }

        ActiveGrid = wantedGrid;
        return Task.FromResult(new SurroundApplyResult { Outcome = SurroundOutcome.Changed });
    }

    public Task<IReadOnlyList<SurroundDisplay>> ListDisplaysAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SurroundDisplay>>([.. ActiveGrid?.Displays ?? []]);

    private static bool Equal(SurroundGrid? left, SurroundGrid? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            _ => left.Rows == right.Rows
                && left.Columns == right.Columns
                && left.Width == right.Width
                && left.Height == right.Height
                && left.Displays.Select(d => d.DisplayId).SequenceEqual(right.Displays.Select(d => d.DisplayId)),
        };
}
