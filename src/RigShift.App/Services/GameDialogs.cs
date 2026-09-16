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

    /// <summary>The installed games of every known source, read off the disk.</summary>
    public Task<IReadOnlyList<InstalledGame>> FindInstalledAsync() => Task.Run(() =>
    {
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
