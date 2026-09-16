namespace RigShift.Core.Games;

/// <summary>
/// What we already know about the big sims: which process is the sim, which one is the launcher or interface in
/// front of it, and whether the session should hang on the latter.
///
/// This is comfort, not the mechanism. The process name is still learned on the first start for everything else, and
/// a template only prefills an entry the user can overwrite – unlike Sherpa's fixed table, which is the only thing
/// standing between its launcher and a game it has never seen. The reason to have it at all: for a sim built like
/// iRacing, learning picks the interface on the first try, and being wrong once on the most-used title is a bad
/// first impression.
/// </summary>
public static class SimTemplates
{
    /// <summary>
    /// Known sims by Steam app id; the ones that are not on Steam are matched by name. Only titles whose process
    /// name could actually be verified are in here – a wrong name is worse than none, because the user would trust
    /// it instead of letting the first start learn the right one. The list grows when someone reports a title.
    /// </summary>
    private static readonly SimTemplate[] Known =
    [
        // iRacing's interface stays open all evening, the sim comes and goes with every "Go Racing".
        new("iRacing", SteamAppId: "266410", GameProcess: "iRacingSim64DX11", LauncherProcess: "iRacingUI"),

        // Content Manager replaces the stock launcher and keeps running while acs.exe races.
        new("Assetto Corsa", SteamAppId: "244210", GameProcess: "acs", LauncherProcess: "Content Manager"),
        new("Assetto Corsa Competizione", SteamAppId: "805550", GameProcess: "AC2-Win64-Shipping", LauncherProcess: null),

        // AVX is the instruction set the build needs, not a variant: there is only this one executable.
        new("Automobilista 2", SteamAppId: "1066890", GameProcess: "AMS2AVX", LauncherProcess: null),
        new("rFactor 2", SteamAppId: "365960", GameProcess: "rFactor2", LauncherProcess: null),
    ];

    /// <summary>The template for a game, or <c>null</c> when we know nothing about it.</summary>
    public static SimTemplate? For(GameLaunch launch, string name)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(name);

        if (launch.Kind == GameLaunchKind.Steam
            && Array.Find(Known, t => t.SteamAppId == launch.Target) is { } bySteam)
        {
            return bySteam;
        }

        return Array.Find(Known, t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fills in what the template knows and the entry does not say yet. Never overwrites a value the user set: a
    /// template that argues with the user is worse than no template.
    /// </summary>
    public static GameEntry Apply(GameEntry game)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (For(game.Launch, game.Name) is not { } template)
        {
            return game;
        }

        GameEntry filled = game.Launch.ProcessName is null && game.Launch.Kind != GameLaunchKind.Executable
            ? game with { Launch = game.Launch with { ProcessName = template.GameProcess } }
            : game;

        if (template.LauncherProcess is { } launcher && filled.LauncherProcessName is null)
        {
            filled = filled with { LauncherProcessName = launcher, EndsWith = SessionEnd.LauncherProcess };
        }

        return filled;
    }
}

/// <summary>What is known about one sim.</summary>
/// <param name="Name">Title as the store names it, used when the game did not come from Steam.</param>
/// <param name="SteamAppId">Steam app id, or <c>null</c> when it is not on Steam.</param>
/// <param name="GameProcess">The process that is the sim itself.</param>
/// <param name="LauncherProcess">The interface or launcher that outlives the sim, or <c>null</c> when there is none.</param>
public sealed record SimTemplate(string Name, string? SteamAppId, string GameProcess, string? LauncherProcess);
