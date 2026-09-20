using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using Serilog;

namespace RigShift.Windows.Games;

/// <summary>
/// Installed Steam games, read straight off the disk: the client's folder from the registry, every library folder
/// from <c>steamapps\libraryfolders.vdf</c> (they live on other drives more often than not), then one
/// <c>appmanifest_&lt;appid&gt;.acf</c> per game with its id, title and install folder.
/// No sign-in and no web API – only installed games matter here, and those are all local.
/// </summary>
public sealed class SteamLibrary
{
    private readonly ILogger _log;
    private readonly Func<string?> _clientFolder;

    /// <param name="clientFolder">Steam's install folder; the registry is asked when omitted (tests pass a temp folder).</param>
    public SteamLibrary(ILogger log, Func<string?>? clientFolder = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<SteamLibrary>();
        _clientFolder = clientFolder ?? FindClientFolder;
    }

    public IReadOnlyList<InstalledGame> Find()
    {
        string? client = _clientFolder();
        if (string.IsNullOrEmpty(client) || !Directory.Exists(client))
        {
            _log.Information("Steam is not installed, or its folder {Folder} is gone", client);
            return [];
        }

        var games = new List<InstalledGame>();
        foreach (string steamApps in LibraryFolders(client))
        {
            games.AddRange(GamesIn(steamApps));
        }

        _log.Information("Steam: {Count} installed games", games.Count);
        return games;
    }

    /// <summary>
    /// Every <c>steamapps</c> folder: the client's own plus the paths from <c>libraryfolders.vdf</c>. The file has
    /// had several shapes over the years; both the old <c>"1" "D:\\Games"</c> and the current block with a
    /// <c>"path"</c> key are handled.
    /// </summary>
    public static IReadOnlyList<string> LibraryFolders(string clientFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientFolder);
        var folders = new List<string>();
        void Add(string root)
        {
            string steamApps = Path.Combine(root, "steamapps");
            if (Directory.Exists(steamApps) && !folders.Contains(steamApps, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(steamApps);
            }
        }

        Add(clientFolder);

        string file = Path.Combine(clientFolder, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(file))
        {
            return folders;
        }

        ValveNode root = ValveDataFormat.Parse(BoundedRead.Text(file));
        ValveNode libraries = root.Child("libraryfolders") ?? root;
        foreach ((_, ValveNode entry) in libraries.Children)
        {
            if (entry.Value("path") is { Length: > 0 } path)
            {
                Add(path);
            }
        }

        // Older files store the path as the value of the numbered key itself ("1" "D:\\SteamLibrary"). Every value is
        // tried; the ones that are not a library (e.g. "TimeNextStatsReport") have no steamapps folder and drop out.
        foreach ((_, string value) in libraries.Values)
        {
            if (value.Length > 0)
            {
                Add(value);
            }
        }

        return folders;
    }

    /// <summary>The games of one <c>steamapps</c> folder, from its <c>appmanifest_*.acf</c> files.</summary>
    public static IReadOnlyList<InstalledGame> GamesIn(string steamAppsFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(steamAppsFolder);
        var games = new List<InstalledGame>();
        if (!Directory.Exists(steamAppsFolder))
        {
            return games;
        }

        foreach (string file in Directory.EnumerateFiles(steamAppsFolder, "appmanifest_*.acf"))
        {
            string text;
            try
            {
                text = BoundedRead.Text(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            ValveNode state = ValveDataFormat.Parse(text).Child("AppState") ?? new ValveNode();
            string? appId = state.Value("appid");
            string? name = state.Value("name");
            string? installDir = state.Value("installdir");
            if (string.IsNullOrEmpty(appId) || string.IsNullOrEmpty(name))
            {
                continue;
            }

            string? folder = string.IsNullOrEmpty(installDir)
                ? null
                : Path.Combine(steamAppsFolder, "common", installDir);

            games.Add(new InstalledGame(name, new GameLaunch
            {
                Kind = GameLaunchKind.Steam,
                Target = appId,
                InstallFolder = folder is not null && Directory.Exists(folder) ? folder : null,
            }));
        }

        return games;
    }

    /// <summary>Steam's folder from the registry; 64-bit machine view first, then the current user's own key.</summary>
    private string? FindClientFolder()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (machine.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") is string path && path.Length > 0)
            {
                return path;
            }

            if (Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("SteamPath") is string user && user.Length > 0)
            {
                return user.Replace('/', '\\');
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "Steam's registry key could not be read");
        }

        return null;
    }
}
