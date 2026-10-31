using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.Services;

/// <summary>
/// What the games pages build without asking anything: the editor for a game, the entries for picked installed games,
/// and the lists the game picker and the window capture show. Shared by the WPF and the Avalonia dialogs.
/// </summary>
public sealed class GameEditorFactory(
    GameEditorServices editorServices,
    ProfileCatalog profiles,
    IGameLibrary library,
    IWindowLayout windows,
    IUsbDeviceList usbDevices,
    SettingsService settings)
{
    private readonly GameCatalog _catalog = editorServices.Catalog;
    private readonly ILogger _log = editorServices.Log.ForContext<GameEditorFactory>();

    /// <summary>The detail's editor for a game: profiles for the choice, the USB devices the tools can wait for.</summary>
    public async Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(game);
        IReadOnlyList<UsbDevice> connected;
        try
        {
            connected = await Task.Run(usbDevices.ConnectedDevices);
        }
        catch (Exception ex) when (ex is Win32Exception or COMException)
        {
            _log.Warning(ex, "USB devices could not be listed for the game editor");
            connected = [];
        }

        // Devices other games and profiles wait for stay selectable while they are off.
        IEnumerable<RuleDevice> known = _catalog.Games
            .Select(g => new RuleDevice { Id = g.AppsWaitForUsbDeviceId, Name = g.AppsWaitForUsbDeviceName })
            .Concat(profiles.Profiles.Select(p => new RuleDevice { Id = p.AppsWaitForUsbDeviceId, Name = p.AppsWaitForUsbDeviceName }));
        var appsWaitDevice = new AppsWaitDeviceChoice(
            game.AppsWaitForUsbDeviceId, game.AppsWaitForUsbDeviceName, connected, known, settings.Current.UsbDeviceNames,
            new UsbDetection(usbDevices, TimeProvider.System));

        return new GameEditorViewModel(game, isNew, new GameEditorContext(profiles.Profiles, _catalog.Games, appsWaitDevice), editorServices);
    }

    /// <summary>
    /// Adds installed games straight from the picker, without the editor: five sims should not mean five trips
    /// through it. Profile and tools are set afterwards on the entry that is now there. Games that are already
    /// configured are left alone – a second entry for the same game helps nobody.
    /// </summary>
    public async Task<AddedGames> AddInstalledAsync(IReadOnlyList<InstalledGame> picked)
    {
        ArgumentNullException.ThrowIfNull(picked);
        var names = _catalog.Games.Select(g => g.Name).ToList();
        var added = new List<GameEntry>();
        int skipped = 0;
        foreach (InstalledGame game in picked)
        {
            if (_catalog.Games.Any(g => g.Launch.Kind == game.Launch.Kind && g.Launch.Target == game.Launch.Target))
            {
                skipped++;
                continue;
            }

            GameEntry entry = SimTemplates.Apply(new GameEntry
            {
                Id = Guid.NewGuid(),
                Name = ProfileEditing.UniqueName(game.Name, names),
                Launch = game.Launch,
            });
            GameLaunch launch = entry.Launch;
            entry = entry with { Launch = await Task.Run(() => Windows.Games.GameLibrary.WithGuessedProcess(launch, _log)) };

            await _catalog.SaveAsync(entry, CancellationToken.None);
            names.Add(entry.Name);
            added.Add(entry);
            _log.Information("Game {Game} added from the installed games", entry.Name);
        }

        return new AddedGames(added, skipped);
    }

#if DEBUG
    private static readonly System.Text.Json.JsonSerializerOptions PreviewJson = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
#endif

    /// <summary>The installed games of every known source, read off the disk.</summary>
    public Task<IReadOnlyList<InstalledGame>> FindInstalledAsync() => Task.Run(() =>
    {
#if DEBUG
        // Developer aid: a demo library (JSON array of name and launch) for screenshots on a machine without stores.
        if (Environment.GetEnvironmentVariable("RIGSHIFT_PREVIEW_GAMES") is { Length: > 0 } demoGames)
        {
            return (IReadOnlyList<InstalledGame>)(System.Text.Json.JsonSerializer.Deserialize<List<InstalledGame>>(
                File.ReadAllText(demoGames), PreviewJson) ?? []);
        }
#endif

        try
        {
            return library.Find();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception or COMException)
        {
            _log.Warning(ex, "Installed games could not be listed");
            return (IReadOnlyList<InstalledGame>)[];
        }
    });

    /// <summary>Every window that is open right now, for the capture list.</summary>
    public IReadOnlyList<OpenWindow> OpenWindows()
    {
        try
        {
            return windows.Open();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Warning(ex, "Open windows could not be listed");
            return [];
        }
    }
}
