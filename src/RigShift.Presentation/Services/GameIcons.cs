using System.Collections.Concurrent;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The games' own icons for cards, picker and tray; looked up off the UI thread and remembered per game, as the cards
/// rebuild after every save.
/// </summary>
internal static class GameIcons
{
    private static readonly ConcurrentDictionary<string, object?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>The game's icon, or <c>null</c> when none was found – then a symbol stands in.</returns>
    public static Task<object?> LoadAsync(GameLaunch launch, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string key = Key(launch);
        return Cache.TryGetValue(key, out object? known)
            ? Task.FromResult(known)
            : Task.Run(() => Cache.GetOrAdd(key, _ => AppIcons.Load(Windows.Games.GameIconSource.Find(launch, log))));
    }

    // The learned process name belongs in the key: it turns a store game into a findable file.
    private static string Key(GameLaunch launch) => $"{launch.Kind}|{launch.Target}|{launch.ProcessName}";
}
