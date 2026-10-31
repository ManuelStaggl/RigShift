using System.Collections.Concurrent;
using System.IO;
using RigShift.Core.Profiles;

namespace RigShift.App.Services;

/// <summary>Program icons for the app picker, the editor and the profile cards, as the running UI's image type.</summary>
internal static class AppIcons
{
    // Only icons that were found: a miss per typed character used to stay in here for good.
    private static readonly ConcurrentDictionary<string, object> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Set once by the app at start; static because list items, built by the view models, load icons.
    /// </summary>
    public static Func<string, object?> Extract { get; set; } = _ => null;

    /// <returns>
    /// The file's icon, or <c>null</c> for a path that is not a file with one. A network path gets none: a share that
    /// sleeps would hold the caller for the network's timeout.
    /// </returns>
    public static object? Load(string? path)
    {
        string file = Environment.ExpandEnvironmentVariables((path ?? string.Empty).Trim().Trim('"'));
        if (!Path.IsPathFullyQualified(file) || LaunchPath.IsNetwork(file))
        {
            return null;
        }

        if (Cache.TryGetValue(file, out object? cached))
        {
            return cached;
        }

        object? icon = File.Exists(file) ? Extract(file) : null;
        return icon is null ? null : Cache.GetOrAdd(file, icon);
    }
}
