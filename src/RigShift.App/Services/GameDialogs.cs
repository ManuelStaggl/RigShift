using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Services;

/// <summary>What came out of adding installed games at once.</summary>
/// <param name="Added">The entries that were created.</param>
/// <param name="Skipped">Games that were picked but already configured.</param>
public sealed record AddedGames(IReadOnlyList<GameEntry> Added, int Skipped);

/// <summary>
/// What the games page asks of the user or builds for them. The page's logic – what happens to unsaved changes, which
/// game is selected after a delete – runs against this, so it can be tested without a window.
/// </summary>
public interface IGamePageDialogs
{
    /// <summary>The detail's editor for a game.</summary>
    Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew);

    /// <summary>Installed games from the picker, saved right away.</summary>
    Task<AddedGames> AddInstalledAsync();

    /// <summary>A program from the file dialog; <c>null</c> when cancelled.</summary>
    string? PickExecutable();

    /// <summary>The game an entry starts: an installed one or a program; <c>null</c> when cancelled.</summary>
    Task<PickedGame?> PickGameAsync();

    /// <summary>The windows to put back, chosen from the open ones; <c>null</c> when cancelled.</summary>
    /// <param name="current">What the entry has now, pre-selected.</param>
    WindowLayout? CaptureWindows(WindowLayout? current);

    Task<bool> ConfirmDeleteAsync(string name);

    /// <param name="targetName">The game the user picked instead, when the question comes from the list.</param>
    Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName);
}

/// <summary>Builds the game detail's editor and opens the pickers, the window capture and the delete confirmation.</summary>
public sealed class GameDialogs : IGamePageDialogs
{
    private readonly GameCatalog _catalog;
    private readonly ProfileCatalog _profiles;
    private readonly IGameLibrary _library;
    private readonly IWindowLayout _windows;
    private readonly IUsbDeviceList _usbDevices;
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly IAppPicker _appPicker;
    private readonly ILogger _log;

    public GameDialogs(
        GameCatalog catalog,
        ProfileCatalog profiles,
        IGameLibrary library,
        IWindowLayout windows,
        IUsbDeviceList usbDevices,
        SettingsService settings,
        HotkeyService hotkeys,
        IAppPicker appPicker,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _profiles = profiles;
        _library = library;
        _windows = windows;
        _usbDevices = usbDevices;
        _settings = settings;
        _hotkeys = hotkeys;
        _appPicker = appPicker;
        _log = log.ForContext<GameDialogs>();
    }

    /// <summary>The detail's editor for a game: profiles for the choice, the USB devices the tools can wait for.</summary>
    public async Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(game);
        IReadOnlyList<UsbDevice> connected;
        try
        {
            connected = await Task.Run(_usbDevices.ConnectedDevices);
        }
        catch (Exception ex) when (ex is Win32Exception or COMException)
        {
            _log.Warning(ex, "USB devices could not be listed for the game editor");
            connected = [];
        }

        // Devices other games and profiles wait for stay selectable while they are off.
        IEnumerable<RuleDevice> known = _catalog.Games
            .Select(g => new RuleDevice { Id = g.AppsWaitForUsbDeviceId, Name = g.AppsWaitForUsbDeviceName })
            .Concat(_profiles.Profiles.Select(p => new RuleDevice { Id = p.AppsWaitForUsbDeviceId, Name = p.AppsWaitForUsbDeviceName }));
        var appsWaitDevice = new AppsWaitDeviceChoice(
            game.AppsWaitForUsbDeviceId, game.AppsWaitForUsbDeviceName, connected, known, _settings.Current.UsbDeviceNames);

        return new GameEditorViewModel(game, isNew, _profiles.Profiles, _catalog.Games, appsWaitDevice, _appPicker, _catalog, _hotkeys, _log);
    }

    /// <summary>
    /// Adds installed games straight from the picker, without the editor: five sims should not mean five trips
    /// through it. Profile and tools are set afterwards on the entry that is now there. Games that are already
    /// configured are left alone – a second entry for the same game helps nobody.
    /// </summary>
    public async Task<AddedGames> AddInstalledAsync()
    {
        IReadOnlyList<InstalledGame> picked =
            await GamePickerWindow.PickManyAsync(System.Windows.Application.Current.MainWindow, this);

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

            await _catalog.SaveAsync(entry, CancellationToken.None);
            names.Add(entry.Name);
            added.Add(entry);
            _log.Information("Game {Game} added from the installed games", entry.Name);
        }

        return new AddedGames(added, skipped);
    }

    /// <summary>The single-choice picker for the "Starts" field of the "Game" tab; <c>null</c> when cancelled.</summary>
    public Task<PickedGame?> PickGameAsync() => GamePickerWindow.PickAsync(System.Windows.Application.Current.MainWindow, this);

    public WindowLayout? CaptureWindows(WindowLayout? current) =>
        WindowCaptureWindow.Capture(System.Windows.Application.Current.MainWindow, this, current);

    /// <summary>A program from the file dialog, for "+ New → Choose a program"; <c>null</c> when cancelled.</summary>
    public string? PickExecutable()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = Loc.Instance["App_FileFilter"],
            Title = Loc.Instance["List_NewProgram"].Replace("_", string.Empty, StringComparison.Ordinal).TrimEnd('…', ' '),
        };
        return dialog.ShowDialog(System.Windows.Application.Current.MainWindow) == true ? dialog.FileName : null;
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
            return _library.Find();
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
            return _windows.Open();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Warning(ex, "Open windows could not be listed");
            return [];
        }
    }

    /// <summary>Delete is destructive: red text, never the accent (R-ACT-3).</summary>
    public async Task<bool> ConfirmDeleteAsync(string name) =>
        await DialogWindow.AskAsync(
            Loc.Instance["Games_DeleteTitle"],
            Loc.Format("Games_DeleteText", name),
            [
                new DialogChoice(Loc.Instance["Common_Delete"], DialogButtonKind.Danger, 1),
                new DialogChoice(Loc.Instance["Common_Cancel"], DialogButtonKind.Secondary, 0),
            ],
            cancelResult: 0) == 1;

    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) =>
        ProfileDialogs.ConfirmUnsavedAsync(name, targetName);
}
