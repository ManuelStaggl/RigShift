using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;

namespace RigShift.Windows.Audio;

/// <summary>
/// <see cref="IAudioController"/> using Core Audio (enumeration, volume) and the undocumented
/// <c>IPolicyConfig</c> COM interface (default endpoint). Interface definition is in <c>legacy/DisplayProfile.ps1</c>.
/// </summary>
public sealed class PolicyConfigAudioController : IAudioController
{
    public Task<IReadOnlyList<AudioDeviceInfo>> ListAsync(AudioDirection direction, CancellationToken cancellationToken)
        => throw new NotImplementedException("Planned for v1 – see docs/PLAN.md, Meilenstein M2.");

    public Task<bool> SetDefaultAsync(AudioEndpoint endpoint, AudioRoleMask roles, CancellationToken cancellationToken)
        => throw new NotImplementedException("Planned for v1 – see docs/PLAN.md, Meilenstein M2.");

    public Task SetVolumeAsync(AudioEndpoint endpoint, int percent, CancellationToken cancellationToken)
        => throw new NotImplementedException("Planned for v1.1 – see docs/ROADMAP.md.");
}
