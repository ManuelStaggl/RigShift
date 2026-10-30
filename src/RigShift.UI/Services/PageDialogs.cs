using Microsoft.UI.Xaml.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;

namespace RigShift.UI.Services;

/// <summary>
/// The questions and builders behind the profiles page, as WinUI content dialogs. The help page's and the setup
/// wizard's members come with those pages (#94, #95); nothing on screen reaches them yet.
/// </summary>
internal sealed class PageDialogs(ProfileEditorFactory factory) : IProfilePageDialogs, IHelpPageDialogs
{
    public Task<Profile> NewFromCurrentAsync() => factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => factory.CreateEditorAsync(profile, isNew);

    /// <summary>Delete is destructive: no accent on the action, Cancel is the default.</summary>
    public async Task<bool> ConfirmDeleteAsync(string name, int ruleCount) =>
        await Ask(
            Loc.Instance["Profile_DeleteTitle"],
            Loc.Format(ruleCount > 0 ? "Profile_DeleteTextWithRules" : "Profile_DeleteText", name),
            Loc.Instance["Profile_Delete"],
            ContentDialogButton.Close) == ContentDialogResult.Primary;

    /// <summary>"Save changes to X?" with Save (the default: the changes were made on purpose), Discard and Cancel.</summary>
    public async Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = App.XamlRoot,
            Title = Loc.Format("Unsaved_Title", name),
            Content = targetName is null ? Loc.Instance["Unsaved_Text"] : Loc.Format("Unsaved_TextSwitch", targetName),
            PrimaryButtonText = Loc.WithoutAccessKey(Loc.Instance["Common_Save"]),
            SecondaryButtonText = Loc.Instance["Common_Discard"],
            CloseButtonText = Loc.Instance["Common_Cancel"],
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => UnsavedChoice.Save,
            ContentDialogResult.Secondary => UnsavedChoice.Discard,
            _ => UnsavedChoice.Cancel,
        };
    }

    // shortcut: the wizard and the help page's files and restore question throw until #95 and #94 build them.
    public Task ShowSetupAssistantAsync() => throw Pending("#95");

    public string? PickZipToSave(string suggestedName) => throw Pending("#94");

    public string? PickZipToOpen() => throw Pending("#94");

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => throw Pending("#94");

    private static NotSupportedException Pending(string issue) => new($"Not in the WinUI app yet ({issue}).");

    private static async Task<ContentDialogResult> Ask(string title, string message, string action, ContentDialogButton defaultButton) =>
        await new ContentDialog
        {
            XamlRoot = App.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = action,
            CloseButtonText = Loc.Instance["Common_Cancel"],
            DefaultButton = defaultButton,
        }.ShowAsync();
}
