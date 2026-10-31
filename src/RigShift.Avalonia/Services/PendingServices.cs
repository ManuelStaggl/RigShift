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

/// <summary>shortcut: no app picker window yet, "Add app" does nothing; it comes with the profiles page (#161).</summary>
internal sealed class NoAppPicker : IAppPicker
{
    public Task<PickedApp?> PickAsync(string? currentPath) => Task.FromResult<PickedApp?>(null);
}

/// <summary>
/// shortcut: the editors through the shared factory, and every question answers "cancel", so nothing is deleted,
/// discarded or restored; the dialogs come with the profiles and help pages (#161, #95).
/// </summary>
internal sealed class PendingDialogs(ProfileEditorFactory factory) : IProfilePageDialogs, IHelpPageDialogs
{
    public Task<Profile> NewFromCurrentAsync() => factory.NewFromCurrentAsync();

    public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => factory.CreateEditorAsync(profile, isNew);

    public Task ShowSetupAssistantAsync() => Task.CompletedTask;

    public Task<bool> ConfirmDeleteAsync(string name, int ruleCount) => Task.FromResult(false);

    public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName) => Task.FromResult(UnsavedChoice.Discard);

    public string? PickZipToSave(string suggestedName) => null;

    public string? PickZipToOpen() => null;

    public Task<bool> ConfirmRestoreAsync(BackupContent content) => Task.FromResult(false);
}
