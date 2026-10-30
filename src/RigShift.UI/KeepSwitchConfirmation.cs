using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.UI;

/// <summary>
/// shortcut: keeps every switch, as with the countdown turned off; the countdown window comes with #95, and this build
/// is not released before it.
/// </summary>
internal sealed class KeepSwitchConfirmation : ISwitchConfirmation
{
    public Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log.Warning("No countdown in this build, the switch to {Profile} is kept", profile?.Name);
        return Task.FromResult(ConfirmationResult.Confirmed);
    }
}
