namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for the automation: which programs run right now.</summary>
public interface IProcessList
{
    /// <summary>Process names without <c>.exe</c>, compared without case.</summary>
    IReadOnlySet<string> RunningProcessNames();
}
