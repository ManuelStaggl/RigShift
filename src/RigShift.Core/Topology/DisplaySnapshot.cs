using RigShift.Core.Profiles;

namespace RigShift.Core.Topology;

/// <summary>
/// The live display topology as reported by the OS at one moment in time.
/// Produced by <see cref="Abstractions.IDisplayConfigurator"/>, consumed by <see cref="TopologyPlanner"/>.
/// </summary>
public sealed record DisplaySnapshot
{
    public required DateTimeOffset TakenAt { get; init; }

    public required IReadOnlyList<AttachedDisplay> Displays { get; init; }
}

/// <summary>A display target the OS currently knows about, whether active or not.</summary>
public sealed record AttachedDisplay
{
    public required DisplayIdentity Identity { get; init; }

    /// <summary>False while the monitor is connected but not ready (e.g. HDMI monitor still waking up).</summary>
    public required bool IsAvailable { get; init; }

    public required bool IsActive { get; init; }

    /// <summary>Current mode if active; null otherwise.</summary>
    public DisplayAssignment? ActiveMode { get; init; }

    /// <summary>
    /// <c>n</c> of the GDI name <c>\\.\DISPLAYn</c> of an active display: the number Windows counts displays with, which the
    /// Windows settings show in most setups (finding HW-04). Null when inactive or unknown.
    /// </summary>
    public int? WindowsNumber { get; init; }

    /// <summary>Opaque OS handle (adapter LUID + target id) valid only for this snapshot. Never persisted.</summary>
    public required object NativeHandle { get; init; }
}
