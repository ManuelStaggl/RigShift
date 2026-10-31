using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// <see cref="IGameLibrary"/> over the known sources. A source that is not installed simply contributes nothing, and
/// a source that throws must not take the others down with it – a missing library is a worse answer than a short one.
/// </summary>
public sealed class GameLibrary : IGameLibrary
{
    private readonly ILogger _log;
    private readonly SteamLibrary _steam;
    private readonly EpicLibrary _epic;
    private readonly UninstallLibrary _uninstall;
    private readonly XboxLibrary _xbox;
    private readonly GogLibrary _gog;
    private readonly UbisoftLibrary _ubisoft;

    public GameLibrary(
        ILogger log, SteamLibrary? steam = null, EpicLibrary? epic = null, UninstallLibrary? uninstall = null, XboxLibrary? xbox = null,
        GogLibrary? gog = null, UbisoftLibrary? ubisoft = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<GameLibrary>();
        _steam = steam ?? new SteamLibrary(log);
        _epic = epic ?? new EpicLibrary(log);
        _uninstall = uninstall ?? new UninstallLibrary(log);
        _xbox = xbox ?? new XboxLibrary(log);
        _gog = gog ?? new GogLibrary(log);
        _ubisoft = ubisoft ?? new UbisoftLibrary(log);
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        var games = new List<InstalledGame>();
        games.AddRange(From("Steam", _steam.Find));
        games.AddRange(From("Epic", _epic.Find));
        games.AddRange(From("Uninstall list", _uninstall.Find));
        games.AddRange(From("Xbox", _xbox.Find));
        games.AddRange(From("GOG", _gog.Find));
        games.AddRange(From("Ubisoft", _ubisoft.Find));

        return [.. games
            .DistinctBy(g => (g.Launch.Kind, g.Launch.Target))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>
    /// The launch with a guessed process name for a store game that has none yet, so "also run when started from the
    /// store" works before the first start through RigShift; otherwise the launch as it is.
    /// </summary>
    public static GameLaunch WithGuessedProcess(GameLaunch launch, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(launch);
        ArgumentNullException.ThrowIfNull(log);
        if (launch.Kind == GameLaunchKind.Executable || launch.ProcessName is not null || launch.InstallFolder is not { Length: > 0 } folder
            || GameExecutable.GuessProcessName(folder, log) is not { } guess)
        {
            return launch;
        }

        log.Information("Process name of {Folder} guessed as {Process}", folder, guess);
        return launch with { ProcessName = guess, ProcessNameGuessed = true };
    }

    private IReadOnlyList<InstalledGame> From(string source, Func<IReadOnlyList<InstalledGame>> find)
    {
        try
        {
            return find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "{Source} games could not be listed, the other sources still count", source);
            return [];
        }
    }
}
