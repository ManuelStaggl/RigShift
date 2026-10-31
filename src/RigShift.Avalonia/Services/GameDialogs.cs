using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Avalonia.Views;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>The questions and pickers behind the games page: the game picker, the window capture, the file dialog.</summary>
internal sealed class GameDialogs(GameEditorFactory factory, PageDialogs pageDialogs, ILogger log) : IGamePageDialogs
{
    private readonly ILogger _log = log.ForContext<GameDialogs>();

    public Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew) => factory.CreateEditorAsync(game, isNew);

    /// <summary>Installed games from the picker with tick boxes, saved right away without the editor.</summary>
    public async Task<AddedGames> AddInstalledAsync() => await factory.AddInstalledAsync(await GamePickerWindow.PickManyAsync(factory));

    public Task<PickedGame?> PickGameAsync() => GamePickerWindow.PickAsync(factory);

    public Task<WindowLayout?> CaptureWindowsAsync(WindowLayout? current) => WindowCaptureWindow.CaptureAsync(factory.OpenWindows(), current);

    /// <summary>A program from the file dialog, for "+ → Choose a program"; <c>null</c> when cancelled.</summary>
    public string? PickExecutable() =>
        NativeWindow.PickFile(App.OwnerHandle, Loc.Instance["App_FileFilter"], Loc.WithoutAccessKey(Loc.Instance["List_NewProgram"]).TrimEnd('…', ' '), null);

    /// <summary>Delete is destructive: no default on the action, Enter does nothing.</summary>
    public Task<bool> ConfirmDeleteAsync(string name) =>
        AskDialog.AskAsync(
            Loc.Instance["Games_DeleteTitle"],
            Loc.Format("Games_DeleteText", name),
            new AskChoice<bool>(Loc.Instance["Common_Cancel"], false),
            new AskChoice<bool>(Loc.Instance["Common_Delete"], true));

    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) => pageDialogs.ConfirmUnsavedAsync(name, targetName);

    public string? CreateShortcutIcon(Guid gameId, string iconFile, string directory) => GameShortcutIcon.Create(gameId, iconFile, directory, _log);

    public void DeleteShortcutIcon(Guid gameId, string directory) => GameShortcutIcon.Delete(gameId, directory, _log);
}
