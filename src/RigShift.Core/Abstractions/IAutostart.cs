namespace RigShift.Core.Abstractions;

/// <summary>Start with Windows. Windows implementation: HKCU <c>Run</c> key with <c>--minimized --autostart</c>.</summary>
public interface IAutostart
{
    bool IsEnabled { get; }

    void SetEnabled(bool enabled);

    /// <summary>
    /// Adds <c>--autostart</c> to an entry written before 4.2, so the next sign-in counts as a start with Windows. Leaves
    /// entries alone that point at another executable.
    /// </summary>
    void UpgradeEntry();
}
