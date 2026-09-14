namespace RigShift.Core.Abstractions;

/// <summary>
/// Persists the communications ducking value from before a profile with
/// <see cref="Profiles.Profile.DisableCommunicationsDucking"/> took over, so a crash, restart or update between that
/// profile and the next one does not leave the Windows setting changed for good (analysis finding B-01).
/// </summary>
public interface IDuckingMemory
{
    /// <summary>The remembered value, or <c>null</c> when nothing is remembered.</summary>
    Task<RememberedDucking?> LoadAsync(CancellationToken cancellationToken);

    Task SaveAsync(int? value, CancellationToken cancellationToken);

    Task ClearAsync(CancellationToken cancellationToken);
}

/// <summary>A remembered ducking value; <see cref="Value"/> <c>null</c> means the registry value was missing.</summary>
public sealed record RememberedDucking(int? Value);
