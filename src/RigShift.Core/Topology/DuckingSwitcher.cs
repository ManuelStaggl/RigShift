using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Topology;

/// <summary>
/// The communications ducking part of a switch, its rollback and the restore at startup.
/// Logs under the orchestrator's context, so the log source stays the same.
/// </summary>
internal sealed class DuckingSwitcher(IDuckingPreference ducking, IDuckingMemory duckingMemory, ILogger log)
{
    private readonly IDuckingPreference _ducking = ducking;

    /// <summary>
    /// The ducking setting from before a profile with <see cref="Profile.DisableCommunicationsDucking"/> took over; restored
    /// by the next profile without it. Persisted, so it survives a crash or restart (analysis finding B-01).
    /// </summary>
    private readonly IDuckingMemory _duckingMemory = duckingMemory;
    private readonly ILogger _log = log;

    /// <summary>
    /// A profile with <see cref="Profile.DisableCommunicationsDucking"/> sets "do nothing" during calls; the value from
    /// before the first such profile is remembered and comes back with the next profile without the flag. Failures are
    /// logged and never fail the switch (the registry value is undocumented).
    /// </summary>
    public async Task SwitchAsync(Profile profile, CancellationToken cancellationToken)
    {
        try
        {
            RememberedDucking? original = await _duckingMemory.LoadAsync(cancellationToken);
            if (profile.DisableCommunicationsDucking)
            {
                int? current = _ducking.Read();
                if (original is null)
                {
                    // Remembered before the registry changes: a crash right after must still find the old value.
                    await _duckingMemory.SaveAsync(current, cancellationToken);
                }

                if (current != CommunicationsDucking.DoNothing)
                {
                    _ducking.Write(CommunicationsDucking.DoNothing);
                    _log.Information("Communications ducking turned off for {Profile} (was {Preference})", profile.Name, current);
                }
            }
            else if (original is not null)
            {
                RestoreRemembered(original, profile.Name);
                await _duckingMemory.ClearAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Communications ducking for {Profile} could not be set", profile.Name);
        }
    }

    /// <summary>
    /// At startup: a remembered value whose profile is no longer active (crash, restart or update in between) comes back
    /// now instead of waiting for the next switch. With a profile that disables ducking still active, it stays remembered.
    /// </summary>
    public async Task RestoreIfUnusedAsync(Profile? activeProfile, CancellationToken cancellationToken)
    {
        if (activeProfile is { DisableCommunicationsDucking: true })
        {
            return;
        }

        try
        {
            if (await _duckingMemory.LoadAsync(cancellationToken) is not { } original)
            {
                return;
            }

            _log.Information("Remembered communications ducking {Preference} found without an active profile that needs it", original.Value);
            RestoreRemembered(original, activeProfile?.Name ?? "startup");
            await _duckingMemory.ClearAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the remembered communications ducking setting at startup failed");
        }
    }

    private void RestoreRemembered(RememberedDucking original, string profileName)
    {
        if (_ducking.Read() != original.Value)
        {
            _ducking.Write(original.Value);
            _log.Information("Communications ducking {Preference} from before restored for {Profile}", original.Value, profileName);
        }
        else
        {
            _log.Debug("Communications ducking already {Preference} as before, nothing to restore for {Profile}", original.Value, profileName);
        }
    }

    public async Task<DuckingRestore?> CaptureAsync(Profile profile, CancellationToken cancellationToken)
    {
        try
        {
            RememberedDucking? remembered = await _duckingMemory.LoadAsync(cancellationToken);
            return !profile.DisableCommunicationsDucking && remembered is null
                ? null
                : new DuckingRestore(_ducking.Read(), remembered);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Could not read the communications ducking setting; rollback will leave it unchanged");
            return null;
        }
    }

    public async Task RestoreAsync(DuckingRestore? restore, CancellationToken cancellationToken)
    {
        if (restore is null)
        {
            return;
        }

        try
        {
            if (_ducking.Read() != restore.Value)
            {
                _ducking.Write(restore.Value);
            }

            if (restore.BeforeProfiles is { } remembered)
            {
                await _duckingMemory.SaveAsync(remembered.Value, cancellationToken);
            }
            else
            {
                await _duckingMemory.ClearAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Warning(ex, "Restoring the communications ducking setting failed");
        }
    }

    internal sealed record DuckingRestore(int? Value, RememberedDucking? BeforeProfiles);
}
