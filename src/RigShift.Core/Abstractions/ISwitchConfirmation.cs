using RigShift.Core.Profiles;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Asks the user to keep a new topology ("Keep these display settings?"). The App shows a countdown window on the
/// new primary display. Only an explicit action confirms – mouse movement proves nothing about a visible picture.
/// </summary>
public interface ISwitchConfirmation
{
    /// <summary>Resolves with <see cref="ConfirmationResult.TimedOut"/> when <paramref name="timeout"/> elapses without an answer.</summary>
    Task<ConfirmationResult> ConfirmAsync(Profile profile, TimeSpan timeout, CancellationToken cancellationToken);
}

public enum ConfirmationResult
{
    Confirmed,
    Rejected,
    TimedOut,
}
