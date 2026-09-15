namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for the keep-awake request.</summary>
public interface IPowerController
{
    /// <summary>True while RigShift holds a request that keeps the system and the displays on.</summary>
    bool IsKeepingAwake { get; }

    /// <summary>
    /// Holds or releases the request against standby, screen saver and display timeout. Windows drops it on its own
    /// when the process ends, so a crash never leaves the machine awake.
    /// </summary>
    void SetKeepAwake(bool keepAwake);
}
