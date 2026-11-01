using System.Collections.Concurrent;
using System.Windows.Media;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// The games' own icons for the cards, the picker and the tray. Looking the file up can mean walking an install
/// folder, so it happens off the UI thread and is remembered per game – the cards are rebuilt after every save.
/// </summary>
internal static class GameIcons
{
    private static readonly ConcurrentDictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <returns>The game's icon, or <c>null</c> when none was found – then a symbol stands in.</returns>
    public static Task<ImageSource?> LoadAsync(GameLaunch launch, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(launch);
        string key = Key(launch);
        return Cache.TryGetValue(key, out ImageSource? known)
            ? Task.FromResult(known)
            : Task.Run(() => Cache.GetOrAdd(key, _ => AppIcons.Load(Windows.Games.GameIconSource.Find(launch, log))));
    }

    /// <summary>The learned process name belongs in the key: it is what turns a store game into a findable file.</summary>
    private static string Key(GameLaunch launch) => $"{launch.Kind}|{launch.Target}|{launch.ProcessName}";
}
