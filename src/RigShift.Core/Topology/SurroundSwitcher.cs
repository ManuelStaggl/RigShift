using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The Surround step of a switch. It runs before the display arrangement, because turning Surround on or off decides
/// which displays Windows sees at all - three monitors become one wide one and back. That also makes it the one step
/// a rollback has to undo first, which is why the state is captured before anything is touched.
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
    /// The Surround state as a setting that can be applied again, or <c>null</c> when the profile leaves Surround alone
    /// or the state cannot be read. A captured state is what the rollback and the crash journal restore.
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

    /// <summary>
    /// Applies what the profile asks for. A profile without a Surround setting is not a profile that wants Surround
    /// off - it is one that does not care, and then nothing happens.
    /// </summary>
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

    /// <summary>Puts a captured state back. Failures are logged, never thrown: a rollback has nothing better to try.</summary>
    internal async Task RestoreAsync(SurroundSetting? captured, CancellationToken cancellationToken)
    {
        if (captured is null)
        {
            return;
        }

        SurroundApplyResult result = await _surround.ApplyAsync(captured, cancellationToken);
        if (result.Outcome == SurroundOutcome.Failed)
        {
            _log.Error("Surround could not be put back: {Message}", result.Message);
        }
        else if (result.Outcome == SurroundOutcome.Changed)
        {
            _log.Information("Surround put back to {State}", captured.Enabled ? "on" : "off");
        }
    }
}
