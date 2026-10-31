using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using RigShift.Windows.Ui;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>The questions and builders behind the profiles and help pages and the setup assistant.</summary>
internal sealed class PageDialogs(
    ProfileEditorFactory factory,
    ProfileEditorServices editorServices,
    IAudioController audio,
    IUsbDeviceList usbDevices,
    IUsbPowerCheck powerCheck,
    ActiveProfileMatcher matcher,
    ISurroundController surround,
    SwitchCoordinator coordinator) : IProfilePageDialogs, IHelpPageDialogs
{
    private const string ZipFilter = "ZIP (*.zip)|*.zip";

    private readonly ILogger _log = editorServices.Log.ForContext<PageDialogs>();

    public Task<Profile> NewFromCurrentAsync() => factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => factory.CreateEditorAsync(profile, isNew);

    /// <summary>Delete is destructive: no default on the action, Enter does nothing.</summary>
    public Task<bool> ConfirmDeleteAsync(string name, int ruleCount) =>
        AskDialog.AskAsync(
            Loc.Instance["Profile_DeleteTitle"],
            Loc.Format(ruleCount > 0 ? "Profile_DeleteTextWithRules" : "Profile_DeleteText", name),
            new AskChoice<bool>(Loc.Instance["Common_Cancel"], false),
            new AskChoice<bool>(Loc.Instance["Profile_Delete"], true));

    /// <summary>"Save changes to X?" with Save (the default: the changes were made on purpose), Discard and Cancel.</summary>
    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) =>
        AskDialog.AskAsync(
            Loc.Format("Unsaved_Title", name),
            targetName is null ? Loc.Instance["Unsaved_Text"] : Loc.Format("Unsaved_TextSwitch", targetName),
            new AskChoice<UnsavedChoice>(Loc.Instance["Common_Cancel"], UnsavedChoice.Cancel),
            new AskChoice<UnsavedChoice>(Loc.Instance["Common_Save"], UnsavedChoice.Save, IsDefault: true),
            new AskChoice<UnsavedChoice>(Loc.Instance["Common_Discard"], UnsavedChoice.Discard));

    public Task ShowSetupAssistantAsync() => ShowSetupAssistantAsync(null);

    /// <summary>The setup assistant; remembers that it was shown, and "Try it" on the last step switches once it closed.</summary>
    /// <param name="previewStep">Debug builds: open at this step with demo profiles, for screenshots.</param>
    internal async Task ShowSetupAssistantAsync(SetupStep? previewStep)
    {
        var viewModel = new SetupWizardViewModel(
            editorServices.Catalog, editorServices.Display, audio, usbDevices, powerCheck, matcher, editorServices.Settings, surround, editorServices.Hotkeys, editorServices.Log);
#if DEBUG
        if (previewStep is { } step)
        {
            await viewModel.PreviewAsync(step);
        }
#else
        _ = previewStep;
#endif
        try
        {
            await editorServices.Settings.UpdateAsync(s => s with { SetupAssistantShown = true }, CancellationToken.None, notify: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Could not remember that the setup assistant was shown");
        }

        _log.Information("Setup assistant opening at step {Step}", viewModel.Step);
        await Views.SetupWizardWindow.ShowAsync(viewModel);
        _log.Information("Setup assistant closed");

        // "Try it" on the last step: the first real switch, with the countdown that takes it back.
        if (viewModel.SwitchAfterClose is { } target)
        {
            await coordinator.SwitchAsync(target);
        }
    }

    public string? PickZipToSave(string suggestedName) => NativeWindow.SaveFile(App.OwnerHandle, ZipFilter, null, suggestedName);

    public string? PickZipToOpen() => NativeWindow.PickFile(App.OwnerHandle, ZipFilter, Loc.WithoutAccessKey(Loc.Instance["About_RestoreBackup"]).TrimEnd('…', ' '), null);

    /// <summary>
    /// Restoring replaces everything, so it is asked like deleting (no default on the action), with everything the
    /// backup would start, end, switch or bind listed under the question.
    /// </summary>
    public Task<bool> ConfirmRestoreAsync(BackupContent content) =>
        AskDialog.AskAsync(
            Loc.Instance["About_RestoreTitle"],
            RestoreQuestion.Message(content),
            RestoreQuestion.Lines(content),
            new AskChoice<bool>(Loc.Instance["Common_Cancel"], false),
            new AskChoice<bool>(Loc.Instance["About_Restore"], true));

    /// <summary>
    /// RigShift ended between changing the screens and the confirmation, the one case the in-process rollback cannot
    /// cover: restoring the layout from before is the default.
    /// </summary>
    public static Task<bool> ConfirmRestoreInterruptedAsync(string targetProfileName) =>
        AskDialog.AskAsync(
            Loc.Instance["Interrupted_Title"],
            Loc.Format("Interrupted_Text", targetProfileName),
            new AskChoice<bool>(Loc.Instance["Interrupted_Keep"], false),
            new AskChoice<bool>(Loc.Instance["Interrupted_Restore"], true, IsDefault: true));

    /// <summary>
    /// Asked before quitting while a game session runs: without RigShift nothing switches back or closes the tools when
    /// the game ends. Like deleting, no default on the action.
    /// </summary>
    public static Task<bool> ConfirmQuitDuringGameAsync(string games) =>
        AskDialog.AskAsync(
            Loc.Instance["QuitGame_Title"],
            Loc.Format("QuitGame_Text", games),
            new AskChoice<bool>(Loc.Instance["QuitGame_Stay"], false),
            new AskChoice<bool>(Loc.Instance["QuitGame_Quit"], true));

    /// <summary>
    /// Told once at startup: the settings file was unusable and defaults are in force. Not a toast – what is gone
    /// (rules, hotkeys, the update choice) changes how the machine behaves.
    /// </summary>
    /// <returns><c>true</c> when the user wants to see the folder with the copy.</returns>
    public static Task<bool> ShowSettingsProblemAsync(SettingsLoadReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        string? copy = report.BackupFile is null ? null : Path.GetFileName(report.BackupFile);
        bool newer = report.Problem == SettingsLoadProblem.FromNewerVersion;
        string text = newer ? Loc.Format("SettingsProblem_NewerText", copy ?? "–")
            : copy is null ? Loc.Instance["SettingsProblem_TextNoCopy"]
            : Loc.Format("SettingsProblem_TextCopy", copy);
        return AskDialog.AskAsync(
            Loc.Instance[newer ? "SettingsProblem_NewerTitle" : "SettingsProblem_Title"],
            text,
            new AskChoice<bool>(Loc.Instance["Common_Close"], false),
            new AskChoice<bool>(Loc.Instance["SettingsProblem_OpenFolder"], true));
    }

    /// <summary>Asked when the same UI exception keeps coming back; restarting is the primary way out.</summary>
    public static Task<RepeatedErrorChoice> AskAboutRepeatedErrorAsync(string message) =>
        AskDialog.AskAsync(
            Loc.Instance["Crash_Title"],
            Loc.Format("Crash_Text", message),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_Continue"], RepeatedErrorChoice.Continue),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_Restart"], RepeatedErrorChoice.Restart, IsDefault: true),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_OpenLog"], RepeatedErrorChoice.OpenLog));
}
