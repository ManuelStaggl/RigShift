using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>The questions and builders behind the profiles page and the setup assistant; the help page's come with #94.</summary>
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

    // shortcut: backup files (#94) are not built yet; nothing on screen reaches them, and the answers do nothing.

    public string? PickZipToSave(string suggestedName) => null;

    public string? PickZipToOpen() => null;

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => Task.FromResult(false);

    /// <summary>Asked when the same UI exception keeps coming back; restarting is the primary way out.</summary>
    public static Task<RepeatedErrorChoice> AskAboutRepeatedErrorAsync(string message) =>
        AskDialog.AskAsync(
            Loc.Instance["Crash_Title"],
            Loc.Format("Crash_Text", message),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_Continue"], RepeatedErrorChoice.Continue),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_Restart"], RepeatedErrorChoice.Restart, IsDefault: true),
            new AskChoice<RepeatedErrorChoice>(Loc.Instance["Crash_OpenLog"], RepeatedErrorChoice.OpenLog));
}
