namespace RigShift.Core.Profiles;

/// <summary>A program to start or end after a confirmed switch.</summary>
public sealed record AppAction
{
    public AppActionKind Kind { get; init; }

    /// <summary>Path to the program; environment variables such as <c>%ProgramFiles%</c> are expanded.</summary>
    public required string Path { get; init; }

    /// <summary>Display name from the app picker ("SimHub" rather than "SimHubWPF"); null for a path typed or browsed by hand.</summary>
    public string? Name { get; init; }

    /// <summary>Command line arguments for <see cref="AppActionKind.Start"/>; ignored when ending an app.</summary>
    public string? Arguments { get; init; }

    /// <summary>Seconds to wait after this entry before the next one runs.</summary>
    public int WaitSeconds { get; init; }

    /// <summary>
    /// For a game entry: whether this program runs before or after the game itself. Ignored in a profile, which has
    /// no game to be before or after. <see cref="AppTiming.BeforeGame"/> is 0 so that every app written before 1.10
    /// keeps its meaning.
    /// </summary>
    public AppTiming When { get; init; }
}

/// <summary>
/// When an app runs relative to the game. Sim racing needs both: wheelbase software wants to be up before anything
/// else sees the device, while SimHub and Crew Chief attach to a session that already runs and therefore come after
/// the sim – Crew Chief has to be there before you get in the car, not before the sim starts.
/// </summary>
public enum AppTiming
{
    /// <summary>Before the game starts – wheelbase software, Trading Paints, anything the game must already see.</summary>
    BeforeGame,

    /// <summary>After the game started – SimHub, Crew Chief, overlays, anything that attaches to a running session.</summary>
    AfterGame,
}

public enum AppActionKind
{
    /// <summary>Start the program unless a process with its name already runs.</summary>
    Start,

    /// <summary>Close the program's windows, end it if it is still running after a grace period.</summary>
    Stop,
}
