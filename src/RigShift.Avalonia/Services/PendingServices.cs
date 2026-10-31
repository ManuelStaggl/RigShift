using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.Avalonia.Services;

/// <summary>shortcut: keeps every switch the user started, like the WinUI build; the countdown window comes with #93.</summary>
internal sealed class KeepSwitchConfirmation : ISwitchConfirmation
{
    public Task<ConfirmationResult> ConfirmAsync(Profile profile, DisplaySnapshot before, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Log.Warning("No countdown yet, the switch to {Profile} is kept", profile?.Name);
        return Task.FromResult(ConfirmationResult.Confirmed);
    }
}

/// <summary>The questions and builders behind the profiles page; the help page's and the wizard's come with #94 and #95.</summary>
internal sealed class PageDialogs(ProfileEditorFactory factory) : IProfilePageDialogs, IHelpPageDialogs
{
    public Task<Profile> NewFromCurrentAsync() => factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => factory.CreateEditorAsync(profile, isNew);

    /// <summary>Delete is destructive: no default on the action, Enter does nothing.</summary>
    public Task<bool> ConfirmDeleteAsync(string name, int ruleCount) =>
        AskDialog.AskAsync(
            Loc.Instance["Profile_DeleteTitle"],
            Loc.Format(ruleCount > 0 ? "Profile_DeleteTextWithRules" : "Profile_DeleteText", name),
            false,
            new AskChoice<bool>(Loc.Instance["Profile_Delete"], true));

    /// <summary>"Save changes to X?" with Save (the default: the changes were made on purpose), Discard and Cancel.</summary>
    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) =>
        AskDialog.AskAsync(
            Loc.Format("Unsaved_Title", name),
            targetName is null ? Loc.Instance["Unsaved_Text"] : Loc.Format("Unsaved_TextSwitch", targetName),
            UnsavedChoice.Cancel,
            new AskChoice<UnsavedChoice>(Loc.Instance["Common_Save"], UnsavedChoice.Save, IsDefault: true),
            new AskChoice<UnsavedChoice>(Loc.Instance["Common_Discard"], UnsavedChoice.Discard));

    // shortcut: wizard (#95) and backup files (#94) are not built yet; nothing on screen reaches them, and the answers do nothing.
    public Task ShowSetupAssistantAsync() => Task.CompletedTask;

    public string? PickZipToSave(string suggestedName) => null;

    public string? PickZipToOpen() => null;

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => Task.FromResult(false);
}
