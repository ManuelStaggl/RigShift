using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;

namespace RigShift.App.Services;

/// <summary>The answer to "the same error keeps coming back".</summary>
public enum RepeatedErrorChoice
{
    Continue,
    Restart,
    OpenLog,
}

/// <summary>What the user chose when leaving a profile with unsaved changes.</summary>
public enum UnsavedChoice
{
    Save,
    Discard,
    Cancel,
}

/// <summary>
/// What the profiles page asks of the user or builds for them. The page's logic – what happens to unsaved changes,
/// which profile is selected after a delete – runs against this, so it can be tested without a window.
/// </summary>
public interface IProfilePageDialogs
{
    /// <summary>A new, unsaved profile from the active displays.</summary>
    Task<Profile> NewFromCurrentAsync();

    /// <summary>The detail's editor for a profile.</summary>
    Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew);

    Task ShowSetupAssistantAsync();

    /// <param name="ruleCount">USB rules that go with the profile.</param>
    Task<bool> ConfirmDeleteAsync(string name, int ruleCount);

    /// <param name="targetName">The profile the user picked instead, when the question comes from the list.</param>
    Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName);
}

/// <summary>What the help page asks of the user: files for backups and support packages, and the restore question.</summary>
public interface IHelpPageDialogs
{
    Task ShowSetupAssistantAsync();

    /// <returns>The chosen path, or <c>null</c> when the user cancelled.</returns>
    string? PickZipToSave(string suggestedName);

    /// <returns>The chosen path, or <c>null</c> when the user cancelled.</returns>
    string? PickZipToOpen();

    Task<bool> ConfirmRestoreAsync(BackupContent content);
}

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
    Task<WindowLayout?> CaptureWindowsAsync(WindowLayout? current);

    Task<bool> ConfirmDeleteAsync(string name);

    /// <param name="targetName">The game the user picked instead, when the question comes from the list.</param>
    Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName);

    /// <summary>The game's icon on a RigShift card, for its desktop shortcut, written to <paramref name="directory"/>.</summary>
    /// <returns>The <c>.ico</c> path, or <c>null</c> when the game's icon could not be read.</returns>
    string? CreateShortcutIcon(Guid gameId, string iconFile, string directory);

    /// <summary>Removes the shortcut icon of a deleted game.</summary>
    void DeleteShortcutIcon(Guid gameId, string directory);
}

/// <summary>What the game picker returned: an installed game, or a program the user browsed for.</summary>
public sealed record PickedGame(InstalledGame? Installed, string? ExecutablePath);
