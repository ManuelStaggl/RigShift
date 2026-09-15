using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

public sealed partial class ProfilesViewModel(ProfileCatalog catalog, SwitchCoordinator coordinator, ProfileDialogs dialogs, ILogger log) : ObservableObject
{
    private readonly ILogger _log = log.ForContext<ProfilesViewModel>();

    public ProfileCatalog Catalog => catalog;

    public SwitchCoordinator Coordinator => coordinator;

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool IsStatusOpen { get; set; }

    [ObservableProperty]
    public partial Wpf.Ui.Controls.InfoBarSeverity StatusSeverity { get; set; }

    [RelayCommand]
    private async Task SaveCurrentAsync()
    {
        if (await dialogs.CreateFromCurrentAsync() is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private Task SetupAssistantAsync() => dialogs.ShowSetupAssistantAsync();

    [RelayCommand]
    private async Task EditAsync(ProfileItem? item)
    {
        if (item is not null && await dialogs.EditAsync(item.Profile) is { } saved)
        {
            ShowStatus(Loc.Format("Status_Saved", saved.Name));
        }
    }

    [RelayCommand]
    private async Task DuplicateAsync(ProfileItem? item)
    {
        if (item is null)
        {
            return;
        }

        Profile copy = item.Profile with
        {
            Id = Guid.NewGuid(),
            Name = ProfileEditing.UniqueName(Loc.Format("Profile_CopyName", item.Name), catalog.Profiles.Select(p => p.Name)),
        };
        await RunStoreActionAsync(() => catalog.SaveAsync(copy, CancellationToken.None), Loc.Format("Status_Duplicated", copy.Name));
    }

    [RelayCommand]
    private async Task ToggleDefaultAsync(ProfileItem? item)
    {
        if (item is not null)
        {
            await RunStoreActionAsync(() => catalog.ToggleDefaultAsync(item.Profile, CancellationToken.None), null);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(ProfileItem? item)
    {
        if (item is not null && await ProfileDialogs.ConfirmDeleteAsync(item.Name))
        {
            await RunStoreActionAsync(() => catalog.DeleteAsync(item.Profile, CancellationToken.None), Loc.Format("Status_Deleted", item.Name));
        }
    }

    [RelayCommand]
    private void CreateShortcut(ProfileItem? item)
    {
        if (item is null || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        string title = "RigShift – " + RigShift.Windows.Shell.ShortcutWriter.SafeFileName(item.Name);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), title + ".lnk");
        try
        {
            RigShift.Windows.Shell.ShortcutWriter.Create(
                file, executable, "apply " + RigShift.Core.Cli.CommandLineArguments.Quote(item.Name), Loc.Format("Shortcut_Description", item.Name));
            _log.Information("Shortcut {File} created for profile {Profile}", file, item.Name);
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            _log.Error(ex, "Shortcut {File} could not be created", file);
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <param name="success">Status after success; <c>null</c> when the card itself shows the result.</param>
    private async Task RunStoreActionAsync(Func<Task> action, string? success)
    {
        try
        {
            await action();
            if (success is not null)
            {
                ShowStatus(success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Profile store action failed");
            ShowStatus(Loc.Format("Status_Error", ex.Message), Wpf.Ui.Controls.InfoBarSeverity.Error);
        }
    }

    /// <summary>
    /// Every switch result also on the profile page: Windows suppresses tray balloons while a full-screen game runs,
    /// which is exactly when RigShift switches (analysis finding I-04).
    /// </summary>
    public void ShowSwitchResult(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        (string title, string text, _) = SwitchMessages.ForNotification(record);
        ShowStatus(title + Environment.NewLine + text, record.Outcome switch
        {
            SwitchOutcome.Applied => Wpf.Ui.Controls.InfoBarSeverity.Success,
            SwitchOutcome.Failed => Wpf.Ui.Controls.InfoBarSeverity.Error,
            _ => Wpf.Ui.Controls.InfoBarSeverity.Warning,
        });
    }

    private void ShowStatus(string message, Wpf.Ui.Controls.InfoBarSeverity severity = Wpf.Ui.Controls.InfoBarSeverity.Success)
    {
        // Closed first: after the user closed the bar with its X, setting true again must be a change (analysis finding I-02).
        IsStatusOpen = false;
        StatusMessage = message;
        StatusSeverity = severity;
        IsStatusOpen = true;
    }

    [RelayCommand]
    private async Task ApplyAsync(ProfileItem? item)
    {
        if (item is not null)
        {
            await coordinator.SwitchAsync(item.Profile);
        }
    }

    [RelayCommand]
    private async Task CheckAsync(ProfileItem? item)
    {
        if (item is not null && await coordinator.CheckAsync(item.Profile) is { } result)
        {
            item.CheckMessage = SwitchMessages.DescribePlan(result.Plan);
        }
    }
}
