namespace RigShift.Core.Abstractions;

/// <summary>
/// OS boundary for the Windows communications setting "When Windows detects communications activity"
/// (HKCU <c>Software\Microsoft\Multimedia\Audio</c>, DWORD <c>UserDuckingPreference</c>). Undocumented, so callers only
/// log failures (docs/PLAN.md, section 6, "Neu für 1.3", item 4).
/// </summary>
public interface IDuckingPreference
{
    /// <summary>The stored value, or <c>null</c> when it is missing (Windows then reduces other sounds by 80 %).</summary>
    int? Read();

    /// <summary>Writes the value; <c>null</c> deletes it, which restores the Windows default.</summary>
    void Write(int? value);
}

/// <summary>Values of <c>UserDuckingPreference</c>.</summary>
public static class CommunicationsDucking
{
    public const int MuteOtherSounds = 0;

    /// <summary>The Windows default, also when the value is missing.</summary>
    public const int ReduceBy80Percent = 1;

    public const int ReduceBy50Percent = 2;

    public const int DoNothing = 3;
}
