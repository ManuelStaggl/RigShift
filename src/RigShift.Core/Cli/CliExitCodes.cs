using RigShift.Core.Topology;

namespace RigShift.Core.Cli;

/// <summary>Process exit codes of <c>RigShift.exe</c>. Part of the public contract.</summary>
public static class CliExitCodes
{
    /// <summary>Applied (also partially, without optional displays), dry run not blocked, or any other command succeeded.</summary>
    public const int Applied = 0;

    /// <summary>The switch or command failed; see output and log.</summary>
    public const int Failed = 1;

    /// <summary>A required display is missing; nothing was changed.</summary>
    public const int Blocked = 2;

    /// <summary>The switch was not confirmed and the previous arrangement was restored.</summary>
    public const int RolledBack = 3;

    public const int ProfileNotFound = 4;

    /// <summary>Unknown command or missing argument.</summary>
    public const int InvalidArguments = 5;

    public static int For(SwitchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome switch
        {
            SwitchOutcome.Applied or SwitchOutcome.AppliedPartially => Applied,
            SwitchOutcome.DryRun => result.Plan.IsBlocked ? Blocked : Applied,
            SwitchOutcome.Blocked => Blocked,
            SwitchOutcome.RolledBack => RolledBack,
            _ => Failed,
        };
    }
}
