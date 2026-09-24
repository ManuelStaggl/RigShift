using RigShift.Core.Abstractions;
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

    public GameLibrary(ILogger log, SteamLibrary? steam = null, EpicLibrary? epic = null, UninstallLibrary? uninstall = null, XboxLibrary? xbox = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<GameLibrary>();
        _steam = steam ?? new SteamLibrary(log);
        _epic = epic ?? new EpicLibrary(log);
        _uninstall = uninstall ?? new UninstallLibrary(log);
        _xbox = xbox ?? new XboxLibrary(log);
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        var games = new List<InstalledGame>();
        games.AddRange(From("Steam", _steam.Find));
        games.AddRange(From("Epic", _epic.Find));
        games.AddRange(From("Uninstall list", _uninstall.Find));
        games.AddRange(From("Xbox", _xbox.Find));

        return [.. games
            .DistinctBy(g => (g.Launch.Kind, g.Launch.Target))
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
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
