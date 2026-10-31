using System.Text.Json.Serialization;

namespace RigShift.Core.Profiles;

/// <summary>A program to start or end after a confirmed switch.</summary>
public sealed record AppAction
{
    /// <summary>The longest window wait when no seconds are given – what an empty field meant up to 4.x.</summary>
    public const int DefaultWindowWaitSeconds = 60;

    private readonly int _seconds;
    private readonly bool _legacyWaitForWindow;
    private AppWait? _wait;

    public AppActionKind Kind { get; init; }

    /// <summary>Path to the program; environment variables such as <c>%ProgramFiles%</c> are expanded.</summary>
    public required string Path { get; init; }

    /// <summary>Display name from the app picker ("SimHub" rather than "SimHubWPF"); null for a path typed or browsed by hand.</summary>
    public string? Name { get; init; }

    /// <summary>Command line arguments for <see cref="AppActionKind.Start"/>; ignored when ending an app.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// How the next entry waits for this one. <c>set</c> rather than <c>init</c>: the JSON source generator then leaves it
    /// alone when the key is missing, so an entry from before 5.0 keeps its meaning from <see cref="WaitSeconds"/> and
    /// <see cref="LegacyWaitForWindow"/>, and is written in the new form on the next save.
    /// </summary>
    public AppWait Wait
    {
        get => _wait ?? (LegacyWindowWait ? AppWait.Window : _seconds > 0 ? AppWait.Fixed : AppWait.None);
        set => _wait = value;
    }

    /// <summary>The pause for <see cref="AppWait.Fixed"/>, the longest wait for <see cref="AppWait.Window"/>.</summary>
    public int WaitSeconds
    {
        get => _wait is null && LegacyWindowWait && _seconds <= 0 ? DefaultWindowWaitSeconds : _seconds;
        init => _seconds = value;
    }

    /// <summary>
    /// "Until its window" up to 4.x: it turned <see cref="WaitSeconds"/> into the longest wait, and none meant a minute.
    /// Only read from old files to give <see cref="Wait"/> their meaning. It reads back <c>false</c> so that it is never
    /// written again: an editor comparing the stored form would otherwise see a change nobody made.
    /// </summary>
    [JsonPropertyName("waitForWindow")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyWaitForWindow
    {
        get => false;
        init => _legacyWaitForWindow = value;
    }

    /// <summary>
    /// For a game entry: whether this program runs before or after the game itself. Ignored in a profile, which has
    /// no game to be before or after. <see cref="AppTiming.BeforeGame"/> is 0 so that every app written before 1.10
    /// keeps its meaning.
    /// </summary>
    public AppTiming When { get; init; }

    // Up to 4.x the window wait applied only to a start; an end with it ticked waited its seconds.
    private bool LegacyWindowWait => _legacyWaitForWindow && Kind == AppActionKind.Start;

    /// <summary>By what the entry does: an old entry equals the new one it amounts to, whatever the fields behind it hold.</summary>
    public bool Equals(AppAction? other) =>
        other is not null
        && Kind == other.Kind
        && Path == other.Path
        && Name == other.Name
        && Arguments == other.Arguments
        && Wait == other.Wait
        && WaitSeconds == other.WaitSeconds
        && When == other.When;

    public override int GetHashCode() => HashCode.Combine(Kind, Path, Name, Arguments, Wait, WaitSeconds, When);
}

/// <summary>How the next app in the list waits for this one.</summary>
public enum AppWait
{
    /// <summary>The next app runs right away.</summary>
    None,

    /// <summary>
    /// For a start: on as soon as the program shows a window ("SimHub is up" instead of a guess at how long it takes),
    /// at the latest after <see cref="AppAction.WaitSeconds"/>.
    /// </summary>
    Window,

    /// <summary>Always <see cref="AppAction.WaitSeconds"/>.</summary>
    Fixed,
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
