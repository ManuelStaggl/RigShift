using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Serilog;
using Wpf.Ui.Controls;

namespace RigShift.App.Services;

/// <summary>Builds the game detail's editor and opens the pickers, the window capture and the delete confirmation.</summary>
public sealed class GameDialogs : IGamePageDialogs
{
    public string? CreateShortcutIcon(Guid gameId, string iconFile, string directory) =>
        GameShortcutIcon.Create(gameId, iconFile, directory, _log);

    public void DeleteShortcutIcon(Guid gameId, string directory) => GameShortcutIcon.Delete(gameId, directory, _log);

    private readonly GameEditorFactory _factory;
    private readonly ILogger _log;

    /// <param name="editorServices">Handed to every editor; its catalog and log serve the dialogs too.</param>
    public GameDialogs(
        GameEditorServices editorServices,
        ProfileCatalog profiles,
        IGameLibrary library,
        IWindowLayout windows,
        IUsbDeviceList usbDevices,
        SettingsService settings)
    {
        ArgumentNullException.ThrowIfNull(editorServices);
        _factory = new GameEditorFactory(editorServices, profiles, library, windows, usbDevices, settings);
        _log = editorServices.Log.ForContext<GameDialogs>();
    }

    public Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew) => _factory.CreateEditorAsync(game, isNew);

    /// <summary>Installed games from the picker, saved right away without the editor.</summary>
    public async Task<AddedGames> AddInstalledAsync() =>
        await _factory.AddInstalledAsync(await GamePickerWindow.PickManyAsync(System.Windows.Application.Current.MainWindow, this));

    /// <summary>The single-choice picker for the "Starts" field of the "Game" tab; <c>null</c> when cancelled.</summary>
    public Task<PickedGame?> PickGameAsync() => GamePickerWindow.PickAsync(System.Windows.Application.Current.MainWindow, this);

    public Task<WindowLayout?> CaptureWindowsAsync(WindowLayout? current) =>
        Task.FromResult(WindowCaptureWindow.Capture(System.Windows.Application.Current.MainWindow, this, current));

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

    /// <summary>The installed games of every known source, read off the disk.</summary>
    public Task<IReadOnlyList<InstalledGame>> FindInstalledAsync() => _factory.FindInstalledAsync();

    /// <summary>Every window that is open right now, for the capture list.</summary>
    public IReadOnlyList<OpenWindow> OpenWindows() => _factory.OpenWindows();

    /// <summary>Delete is destructive: red text, never the accent.</summary>
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
