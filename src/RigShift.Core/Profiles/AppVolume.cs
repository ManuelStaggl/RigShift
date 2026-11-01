namespace RigShift.Core.Profiles;

/// <summary>
/// The volume one program plays at after the switch, as in the Windows volume mixer: in the rig Discord quieter and
/// music off, at the desk the other way round. Programs not listed keep their volume.
/// </summary>
public sealed record AppVolume
{
    /// <summary>The program's file name, e.g. <c>Discord.exe</c>; every running copy is set.</summary>
    public required string Executable { get; init; }

    /// <summary>0–100. <c>set</c>: an <c>init</c> initializer is skipped when the key is missing.</summary>
    public int VolumePercent { get; set; } = 100;

    public bool Muted { get; init; }
}
