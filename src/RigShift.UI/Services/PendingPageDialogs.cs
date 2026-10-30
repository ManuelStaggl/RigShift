using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;

namespace RigShift.UI.Services;

/// <summary>
/// Lets the shared view models be built before their WinUI dialogs exist. The overview shows no command that reaches
/// these; the profile editor (#92), the setup wizard and the help page (#94, #95) replace them.
/// </summary>
// shortcut: every call throws; replace member by member as those pages land.
internal sealed class PendingPageDialogs : IProfilePageDialogs, IHelpPageDialogs
{
    public Task<Profile> NewFromCurrentAsync() => throw Pending("#92");

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => throw Pending("#92");

    public Task ShowSetupAssistantAsync() => throw Pending("#95");

    public Task<bool> ConfirmDeleteAsync(string name, int ruleCount) => throw Pending("#92");

    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) => throw Pending("#92");

    public string? PickZipToSave(string suggestedName) => throw Pending("#94");

    public string? PickZipToOpen() => throw Pending("#94");

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => throw Pending("#94");

    private static NotSupportedException Pending(string issue) => new($"Not in the WinUI app yet ({issue}).");
}
