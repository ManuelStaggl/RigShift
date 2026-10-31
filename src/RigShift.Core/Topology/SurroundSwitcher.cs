using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The Surround step of a switch. It runs first because Surround decides which displays Windows sees, so a rollback
/// undoes it first too.
/// </summary>
internal sealed class SurroundSwitcher
{
    private readonly ISurroundController _surround;
    private readonly ILogger _log;

    internal SurroundSwitcher(ISurroundController surround, ILogger log)
    {
        _surround = surround;
        _log = log;
    }

    /// <summary>
    /// The Surround state for the rollback and the crash journal; <c>null</c> when the profile leaves Surround alone or
    /// it cannot be read.
    /// </summary>
    internal async Task<SurroundSetting?> CaptureAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.Surround is null)
        {
            return null;
        }

        SurroundState state = await _surround.QueryAsync(cancellationToken);
        if (state.Availability != SurroundAvailability.Available)
        {
            return null;
        }

        // Only one grid is ever restored: a second one would need its own displays, and no consumer card drives two.
        return state.Grids.Count > 0
            ? new SurroundSetting { Enabled = true, Grid = state.Grids[0] }
            : new SurroundSetting { Enabled = false };
    }

    /// <summary>A profile without a Surround setting does not care about Surround; it does not want it off.</summary>
    internal async Task<SurroundApplyResult> SwitchAsync(Profile profile, CancellationToken cancellationToken)
    {
        if (profile.Surround is not { } wanted)
        {
            return SurroundApplyResult.NotConfigured;
        }

        SurroundApplyResult result = await _surround.ApplyAsync(wanted, cancellationToken);
        switch (result.Outcome)
        {
            case SurroundOutcome.Changed:
                _log.Information("Surround for {Profile}: {State}", profile.Name, wanted.Enabled ? "switched on" : "switched off");
                break;
            case SurroundOutcome.Unchanged:
                _log.Information("Surround for {Profile} was already as wanted", profile.Name);
                break;
            case SurroundOutcome.NotAvailable:
                _log.Information("Surround for {Profile} left alone: {Message}", profile.Name, result.Message);
                break;
            case SurroundOutcome.Failed:
                _log.Error("Surround for {Profile} failed: {Message}", profile.Name, result.Message);
                break;
            default:
                break;
        }

        return result;
    }

    /// <summary>
    /// Never throws: a rollback has nothing better to try. Returns the driver's reason when Surround could not be put
    /// back.
    /// </summary>
    internal async Task<string?> RestoreAsync(SurroundSetting? captured, CancellationToken cancellationToken)
    {
        if (captured is null)
        {
            return null;
        }

        SurroundApplyResult result = await _surround.ApplyAsync(captured, cancellationToken);
        if (result.Outcome == SurroundOutcome.Failed)
        {
            _log.Error("Surround could not be put back: {Message}", result.Message);
            return result.Message ?? "Surround could not be put back.";
        }

        if (result.Outcome == SurroundOutcome.Changed)
        {
            _log.Information("Surround put back to {State}", captured.Enabled ? "on" : "off");
        }

        return null;
    }
}
