using RigShift.Core.Profiles;
using RigShift.Core.Topology;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Asks the user to keep a new topology ("Keep these display settings?"). The App shows a countdown window on the
/// new primary display. Only an explicit action confirms – mouse movement proves nothing about a visible picture.
/// </summary>
public interface ISwitchConfirmation
{
    /// <param name="before">The arrangement the switch started from; the window shows it beside the new one.</param>
    /// <returns>Resolves with <see cref="ConfirmationResult.TimedOut"/> when <paramref name="timeout"/> elapses without an answer.</returns>
    Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum ConfirmationResult
{
    Confirmed,
    Rejected,
    TimedOut,

    /// <summary>RigShift is exiting (or the switch was cancelled otherwise) while the countdown ran (finding HW-07).</summary>
    Cancelled,
}
