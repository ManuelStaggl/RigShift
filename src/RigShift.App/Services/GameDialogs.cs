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

/// <summary>Opens the game editor, the installed-games picker, the window capture and the delete confirmation.</summary>
public sealed class GameDialogs
{
    private readonly GameCatalog _catalog;
    private readonly ProfileCatalog _profiles;
    private readonly IGameLibrary _library;
    private readonly IWindowLayout _windows;
    private readonly IUsbDeviceList _usbDevices;
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;
    private readonly ILogger _log;

    public GameDialogs(
        GameCatalog catalog,
        ProfileCatalog profiles,
        IGameLibrary library,
        IWindowLayout windows,
        IUsbDeviceList usbDevices,
        SettingsService settings,
        HotkeyService hotkeys,
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
        _log = log.ForContext<GameDialogs>();
    }

    /// <returns>The saved game, or <c>null</c> if cancelled.</returns>
    public Task<GameEntry?> CreateAsync()
    {
        string name = ProfileEditing.UniqueName(Loc.Instance["Games_NewName"], _catalog.Games.Select(g => g.Name));
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = name,
            Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = string.Empty },
        };
        return ShowAsync(game, isNew: true);
    }

    public Task<GameEntry?> EditAsync(GameEntry game) => ShowAsync(game, isNew: false);

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

    public static async Task<bool> ConfirmDeleteAsync(string name)
    {
        var dialog = new MessageBox
        {
            Title = Loc.Instance["Games_DeleteTitle"],
            Content = Loc.Format("Games_DeleteText", name),
            PrimaryButtonText = Loc.Instance["Common_Delete"],
            CloseButtonText = Loc.Instance["Common_Cancel"],
        };
        return await dialog.ShowDialogAsync() == MessageBoxResult.Primary;
    }

    private async Task<GameEntry?> ShowAsync(GameEntry game, bool isNew)
    {
        IReadOnlyList<string> otherNames = [.. _catalog.Games.Where(g => g.Id != game.Id).Select(g => g.Name)];
        var model = new GameEditorViewModel(game, _profiles.Profiles, otherNames, isNew);
        FillUsbDevices(model, game);

        var window = new GameEditorWindow(model, this) { Owner = System.Windows.Application.Current.MainWindow };

        // The editor records key combinations; the global hotkeys must not swallow them while it is open.
        _hotkeys.Suspend();
        try
        {
            if (window.ShowDialog() != true)
            {
                return null;
            }
        }
        finally
        {
            _hotkeys.Resume();
        }

        GameEntry saved = model.ToGame();
        await _catalog.SaveAsync(saved, CancellationToken.None);
        _log.Information("Game {Game} saved", saved.Name);
        return saved;
    }

    private void FillUsbDevices(GameEditorViewModel model, GameEntry game)
    {
        IReadOnlyList<UsbDevice> connected;
        try
        {
            connected = _usbDevices.ConnectedDevices();
        }
        catch (Exception ex) when (ex is Win32Exception or COMException)
        {
            _log.Warning(ex, "USB devices could not be listed for the game editor");
            connected = [];
        }

        IEnumerable<RuleDevice> saved = _catalog.Games
            .Select(g => new RuleDevice { Id = g.AppsWaitForUsbDeviceId, Name = g.AppsWaitForUsbDeviceName })
            .Concat(_profiles.Profiles.Select(p => new RuleDevice { Id = p.AppsWaitForUsbDeviceId, Name = p.AppsWaitForUsbDeviceName }));

        model.UsbDevices.Add(new Choice(null, Loc.Instance["Editor_AppsWaitNone"]));
        var devices = new System.Collections.ObjectModel.ObservableCollection<Choice>();
        UsbDeviceChoices.Fill(devices, model.UsbWindowsNames, connected, saved, _settings.Current.UsbDeviceNames);
        foreach (Choice device in devices)
        {
            model.UsbDevices.Add(device);
        }

        string? waitFor = UsbDeviceIds.Normalize(game.AppsWaitForUsbDeviceId);
        model.SelectedUsbDevice = model.UsbDevices.FirstOrDefault(c => c.Key == waitFor) ?? model.UsbDevices[0];
    }
}
