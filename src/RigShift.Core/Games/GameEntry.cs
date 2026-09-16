using System.Text.Json.Serialization;
using RigShift.Core.Profiles;

namespace RigShift.Core.Games;

/// <summary>
/// A game with everything that belongs to a session of it: the display profile to switch to, the companion apps, and
/// what happens once it ends. Deliberately not a <see cref="Profile"/>: a profile is a state of the machine, a game
/// has a start and an end.
/// </summary>
public sealed record GameEntry
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Optional symbol key, one of <see cref="ProfileIcons.All"/>; unknown keys show the RigShift symbol.</summary>
    public string? Icon { get; init; }

    /// <summary>How the game is started.</summary>
    public required GameLaunch Launch { get; init; }

    /// <summary>
    /// Display profile applied before the game starts, or <c>null</c> to leave the machine as it is. Null is the
    /// default on purpose: a game entry that only starts programs must be possible.
    /// </summary>
    public Guid? ProfileId { get; init; }

    /// <summary>
    /// Programs to start or end around the game, in order. Same type as in a profile, so the app picker, the editor
    /// and <c>ProcessAppLauncher</c> are reused unchanged. <c>set</c>: the JSON source generator skips the
    /// initializer of an <c>init</c> property when the key is missing.
    /// </summary>
    public IReadOnlyList<AppAction> Apps { get; set; } = [];

    /// <summary>
    /// Where the helper windows belong, applied after the programs before the game were started and before the game
    /// itself – dragging SimHub and Crew Chief back into place after every switch is exactly the chore this is for.
    /// <c>null</c> when nothing was captured.
    /// </summary>
    public WindowLayout? WindowLayout { get; init; }

    /// <summary>USB device (<c>VID_xxxx&amp;PID_xxxx</c>) the apps wait for, e.g. the wheelbase; <c>null</c> to start right away.</summary>
    public string? AppsWaitForUsbDeviceId { get; init; }

    /// <summary>Name of <see cref="AppsWaitForUsbDeviceId"/> when it was picked, for messages while it is not connected.</summary>
    public string? AppsWaitForUsbDeviceName { get; init; }

    /// <summary>What happens when the game ends. Default <see cref="GameExitKind.Stay"/> – see <see cref="GameExitAction"/>.</summary>
    public GameExitAction Exit { get; set; } = GameExitAction.Stay;

    /// <summary>
    /// What counts as "the game is over". iRacing is the reason this is a choice: its interface
    /// (<c>iRacingUI</c>) stays open all evening while the sim itself (<c>iRacingSim64DX11</c>) starts at "Go
    /// Racing" and ends again on every return to the menu. Hanging the session on the sim would end it between two
    /// races; hanging it on the interface ends it when the user is really done. Assetto Corsa with Content Manager,
    /// rFactor 2, Automobilista 2 and DCS are built the same way.
    /// </summary>
    public SessionEnd EndsWith { get; set; } = SessionEnd.GameProcess;

    /// <summary>
    /// Process name of the launcher or interface for <see cref="SessionEnd.LauncherProcess"/>, without extension.
    /// Prefilled from <see cref="SimTemplates"/> where the game is a known sim.
    /// </summary>
    public string? LauncherProcessName { get; init; }

    /// <summary>System-wide key combination that starts this game while the tray app runs; <c>null</c> for none.</summary>
    public Hotkey? Hotkey { get; init; }

    /// <summary>
    /// React when the game is started outside RigShift (straight from Steam) by running the same session. Only
    /// possible once <see cref="GameLaunch.ProcessName"/> is known, which the first start learns.
    /// </summary>
    public bool StartWithGame { get; init; }

    /// <summary>Longer names push the buttons off the card, as with a profile.</summary>
    public const int MaxNameLength = Profile.MaxNameLength;
}

/// <summary>Where a game comes from and how it is started.</summary>
public sealed record GameLaunch
{
    public required GameLaunchKind Kind { get; init; }

    /// <summary>
    /// Executable path for <see cref="GameLaunchKind.Executable"/>, the Steam app id for
    /// <see cref="GameLaunchKind.Steam"/>, the Epic <c>AppName</c> for <see cref="GameLaunchKind.Epic"/>.
    /// </summary>
    public required string Target { get; init; }

    /// <summary>Command line arguments; for a store launch they are passed to the store's URI where it supports them.</summary>
    public string? Arguments { get; init; }

    /// <summary>
    /// Install folder from the detection. Not used to start the game – it is the filter that tells the game's own
    /// process apart from launcher, overlay and helper processes while learning.
    /// </summary>
    public string? InstallFolder { get; init; }

    /// <summary>
    /// Process name (no extension, as Task Manager shows it), learned on the first start and kept from then on.
    /// <c>null</c> until then; for <see cref="GameLaunchKind.Executable"/> it follows from the path and is not stored.
    /// </summary>
    public string? ProcessName { get; init; }

    /// <summary>The URI a store launch goes through, or <c>null</c> for <see cref="GameLaunchKind.Executable"/>.</summary>
    [JsonIgnore]
    public string? Uri => Kind switch
    {
        GameLaunchKind.Steam => $"steam://rungameid/{Target}",
        GameLaunchKind.Epic => $"com.epicgames.launcher://apps/{Target}?action=launch&silent=true",
        _ => null,
    };

    /// <summary>
    /// The process name to look for, or <c>null</c> when it still has to be learned. A store launch hands us the
    /// store client's process, never the game's, so there the learned name is the only source.
    /// </summary>
    public string? KnownProcessName() => Kind == GameLaunchKind.Executable
        ? Path.GetFileNameWithoutExtension(Environment.ExpandEnvironmentVariables(Target.Trim().Trim('"')))
        : ProcessName;
}

public enum GameLaunchKind
{
    /// <summary>A program path, started directly – the only kind that hands us the game's own process.</summary>
    Executable,

    /// <summary>Steam app id, started through <c>steam://rungameid/</c> so overlay, anti-cheat and DRM see the client.</summary>
    Steam,

    /// <summary>Epic <c>AppName</c>, started through <c>com.epicgames.launcher://</c> for the same reason.</summary>
    Epic,
}

/// <summary>What RigShift does once the game has ended.</summary>
public sealed record GameExitAction
{
    public required GameExitKind Kind { get; init; }

    /// <summary>Target profile for <see cref="GameExitKind.Profile"/>; ignored otherwise.</summary>
    public Guid? ProfileId { get; init; }

    /// <summary>
    /// End the companion apps from <see cref="GameEntry.Apps"/>. Independent of the display side. <c>set</c> instead
    /// of <c>init</c>, because the JSON source generator skips an <c>init</c> initializer when the key is missing –
    /// and the wanted default here is <c>true</c>.
    /// </summary>
    public bool StopApps { get; set; } = true;

    /// <summary>
    /// The default: end the companion apps, leave the displays alone. A switch that happens by itself right after a
    /// game ended is the more unpleasant failure case, so it is never the default.
    /// </summary>
    public static GameExitAction Stay { get; } = new() { Kind = GameExitKind.Stay };
}

/// <summary>What the end of a game session hangs on.</summary>
public enum SessionEnd
{
    /// <summary>The game's own process ended. Right for everything that is one program.</summary>
    GameProcess,

    /// <summary>
    /// The launcher or interface ended. Right for sims whose menu outlives the sim – otherwise the session would end
    /// every time the user goes back to the menu between two races.
    /// </summary>
    LauncherProcess,
}

public enum GameExitKind
{
    /// <summary>Leave the displays as they are.</summary>
    Stay,

    /// <summary>Switch back to the profile that was active before the game started.</summary>
    PreviousProfile,

    /// <summary>Switch to <see cref="GameExitAction.ProfileId"/>.</summary>
    Profile,
}
