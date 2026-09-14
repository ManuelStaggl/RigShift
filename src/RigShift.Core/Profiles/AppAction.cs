namespace RigShift.Core.Profiles;

/// <summary>A program to start or end after a confirmed switch (docs/PLAN.md, section 6, point 5).</summary>
public sealed record AppAction
{
    public AppActionKind Kind { get; init; }

    /// <summary>Path to the program; environment variables such as <c>%ProgramFiles%</c> are expanded.</summary>
    public required string Path { get; init; }

    /// <summary>Command line arguments for <see cref="AppActionKind.Start"/>; ignored when ending an app.</summary>
    public string? Arguments { get; init; }

    /// <summary>Seconds to wait after this entry before the next one runs.</summary>
    public int WaitSeconds { get; init; }
}

public enum AppActionKind
{
    /// <summary>Start the program unless a process with its name already runs.</summary>
    Start,

    /// <summary>Close the program's windows, end it if it is still running after a grace period.</summary>
    Stop,
}
