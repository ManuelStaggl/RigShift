using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The profiles page: the master list on the left, the selected profile's detail (its editor) on the right (R-NAV-2).
/// Leaving a profile with unsaved changes asks first (R-NAV-3); the head shows what a switch would do right now (F2 phase 0).
/// </summary>
public sealed partial class ProfilesViewModel : MasterDetailViewModel<ProfileItem, ProfileEditorViewModel>
{
    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IProfilePageDialogs _dialogs;
    private readonly SettingsService _settings;
    private readonly IDisplayConfigurator _display;
    private readonly TopologyPlanner _planner;
    private readonly IDesktopIcons _desktopIcons;
    private readonly Dictionary<Guid, TopologyPlan> _plans = [];

    public ProfilesViewModel(
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        IProfilePageDialogs dialogs,
        SettingsService settings,
        IDisplayConfigurator display,
        TopologyPlanner planner,
        IDesktopIcons desktopIcons,
        ILogger log)
        : base(log?.ForContext<ProfilesViewModel>() ?? throw new ArgumentNullException(nameof(log)))
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(desktopIcons);
        _catalog = catalog;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _settings = settings;
        _display = display;
        _planner = planner;
        _desktopIcons = desktopIcons;

        // The list follows the profiles; the status lines and the plans follow every read of the displays – after a
        // display change, a switch or a reload – without reading them once more (v4 finding A-03).
        catalog.ProfilesChanged += (_, _) => OnUi(Rebuild);
        catalog.DisplaysRefreshed += (_, snapshot) => OnUi(() => ShowPlans(snapshot));

        // A profile's USB rules live in the settings: the assistant or a restore can change them under the editor.
        settings.Changed += (_, _) => OnUi(() =>
        {
            UpdateStatuses();
            RecheckEditor();
        });
        coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching))
            {
                OnUi(() =>
                {
                    IsBusy = coordinator.IsSwitching;
                    UpdateHead();
                });
            }
        };
        Loc.Instance.PropertyChanged += (_, _) => OnUi(UpdateStatuses);
        Rebuild();
        if (catalog.LastSnapshot is { } snapshot)
        {
            ShowPlans(snapshot);
        }
        else
        {
            _ = RefreshPlansAsync();
        }
    }

    public ProfileCatalog Catalog => _catalog;

    /// <summary>What Windows shows right now; the empty page offers it as the first profile (X-01).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TopologyDisplay> CurrentTopology { get; private set; } = [];

    /// <summary>A switch runs: every trigger is locked, not hidden (R-FLOW-1).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand), nameof(TestCommand))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    public partial StatusKind HeadStatusKind { get; private set; }

    [ObservableProperty]
    public partial string HeadStatusText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SwitchCommand))]
    public partial bool CanSwitch { get; private set; }

    [ObservableProperty]
    public partial bool IsSelectedActive { get; private set; }

    [ObservableProperty]
    public partial bool IsSelectedDefault { get; private set; }

    /// <summary>Switching is the accent action only when it would change something: not while the profile is
    /// already active and not while unsaved edits make Save the primary (B-03).</summary>
    [ObservableProperty]
    public partial bool SwitchIsPrimary { get; private set; }

    [ObservableProperty]
    public partial string SwitchLabel { get; private set; } = string.Empty;

    /// <summary>The names of the missing displays; the head only counts them (P-01).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedMessage))]
    public partial string? BlockedMessage { get; private set; }

    public bool HasBlockedMessage => BlockedMessage is not null;

    protected override IEnumerable<ProfileItem> CatalogItems => _catalog.Items;

    protected override string UnnamedText => Loc.Instance["Editor_NewName"];

    /// <summary>
    /// Every switch result also on the profile page: Windows suppresses tray balloons while a full-screen game runs,
    /// which is exactly when RigShift switches (analysis finding I-04).
    /// </summary>
    public void ShowSwitchResult(SwitchRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        (string title, string text, _) = SwitchMessages.ForNotification(record);
        ShowDetail(title + " " + text, record.Outcome switch
        {
            SwitchOutcome.Applied => InfoKind.Info,
            SwitchOutcome.Failed => InfoKind.Error,
            _ => InfoKind.Warn,
        });
    }

    protected override Task<ProfileEditorViewModel> CreateEditorAsync(ProfileItem item) => _dialogs.CreateEditorAsync(item.Profile, item.IsNew);

    protected override Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? target) => _dialogs.ConfirmUnsavedAsync(name, target);

    protected override Task<bool> ConfirmDeleteAsync(ProfileItem item) => _dialogs.ConfirmDeleteAsync(item.Name, _catalog.RuleCount(item.Profile.Id));

    protected override Task DeleteStoredAsync(ProfileItem item) => _catalog.DeleteAsync(item.Profile, CancellationToken.None);

    protected override bool StoredChanged(ProfileEditorViewModel editor) =>
        !StoredForm.Same(_catalog.Find(editor.Id), editor.Saved) || editor.Rules.ChangedOnDisk(_settings.Current.AutomationRules);

    protected override void OnEditorLoaded(ProfileEditorViewModel editor, ProfileItem item)
    {
        if (_plans.TryGetValue(item.Profile.Id, out TopologyPlan? plan))
        {
            editor.ShowPlan(plan);
        }
    }

    protected override void OnEditorReplaced() => RestoreDesktopIconsCommand.NotifyCanExecuteChanged();

    protected override void OnEditorPropertyChanged(ProfileEditorViewModel editor, string? propertyName)
    {
        if (propertyName == nameof(ProfileEditorViewModel.HasDesktopIcons))
        {
            RestoreDesktopIconsCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task NewFromCurrentAsync()
    {
        if (!await ConfirmLeaveAsync())
        {
            return;
        }

        Profile profile = await _dialogs.NewFromCurrentAsync();
        BeginNew(new ProfileItem(profile, _settings.Current.UsbDeviceNames) { IsNew = true });
        Log.Information("New profile started from the current arrangement with {Count} displays", profile.Displays.Count);
    }

    [RelayCommand]
    private Task SetupAssistantAsync() => _dialogs.ShowSetupAssistantAsync();

    [RelayCommand(CanExecute = nameof(CanSwitch))]
    private async Task SwitchAsync()
    {
        if (SelectedItem is { IsNew: false } item)
        {
            await _coordinator.SwitchAsync(item.Profile);
        }
    }

    private bool CanTest() => !IsBusy && SelectedItem is { IsNew: false };

    /// <summary>Dry run against the live displays; the result lands in the bar above the tabs.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestAsync()
    {
        if (SelectedItem is not { IsNew: false } item || await _coordinator.CheckAsync(item.Profile) is not { } result)
        {
            return;
        }

        string text = SwitchMessages.DescribePlan(result.Plan).Replace(Environment.NewLine, " ", StringComparison.Ordinal);
        ShowDetail(Loc.Format("Status_Tested", text), result.Plan.IsBlocked ? InfoKind.Error : result.Plan.Warnings.Count > 0 ? InfoKind.Warn : InfoKind.Info);
        _plans[item.Profile.Id] = result.Plan;
        Editor?.ShowPlan(result.Plan);
        UpdateStatuses();
    }

    /// <summary>A copy of the profile as saved; unsaved changes are settled first, so the copy has them or not on purpose.</summary>
    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (!await ConfirmLeaveAsync() || SelectedItem is not { IsNew: false } item)
        {
            return;
        }

        Profile copy = item.Profile with
        {
            Id = Guid.NewGuid(),
            Name = ProfileEditing.UniqueName(Loc.Format("Profile_CopyName", item.Name), _catalog.Profiles.Select(p => p.Name)),
        };
        SelectAfterRebuild(copy.Id);
        await RunStoreActionAsync(() => _catalog.SaveAsync(copy, CancellationToken.None), Loc.Format("Status_Duplicated", copy.Name));
    }

    [RelayCommand]
    private async Task ToggleDefaultAsync()
    {
        if (SelectedItem is { IsNew: false } item)
        {
            await RunStoreActionAsync(() => _catalog.ToggleDefaultAsync(item.Profile, CancellationToken.None), null);
        }
    }

    [RelayCommand]
    private void CreateShortcut()
    {
        if (SelectedItem is not { IsNew: false } item || Environment.ProcessPath is not { } executable)
        {
            return;
        }

        string title = "RigShift – " + RigShift.Windows.Shell.ShortcutWriter.SafeFileName(item.Name);
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), title + ".lnk");
        try
        {
            RigShift.Windows.Shell.ShortcutWriter.Create(
                file, executable, "apply " + RigShift.Core.Cli.CommandLineArguments.Quote(item.Name), Loc.Format("Shortcut_Description", item.Name));
            Log.Information("Shortcut {File} created for profile {Profile}", file, item.Name);
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            Log.Error(ex, "Shortcut {File} could not be created", file);
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    private bool CanRestoreDesktopIcons() => Editor is { HasDesktopIcons: true };

    /// <summary>
    /// The saved desktop icons and nothing else: Windows reshuffles them now and then while the profile is already
    /// active, and a full switch is a heavy way to get them back. Takes what the editor shows – a layout captured a
    /// moment ago counts, saved or not.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanRestoreDesktopIcons))]
    private async Task RestoreDesktopIconsAsync()
    {
        if (Editor is not { DesktopIcons: { IsEmpty: false } layout } editor)
        {
            return;
        }

        // The shell call waits up to ten seconds for a hanging Explorer; never on the UI thread.
        DesktopIconResult result = await Task.Run(() => _desktopIcons.Restore(layout));
        Log.Information("Desktop icons of {Profile} put back on request: {Outcome}, {Placed} placed, {Missing} missing",
            editor.Name, result.Outcome, result.Placed, result.Missing);

        switch (result.Outcome)
        {
            case DesktopIconOutcome.Restored when result.Missing > 0:
                ShowDetail(Loc.Format("Status_IconsRestoredMissing", result.Placed, result.Missing), InfoKind.Warn);
                break;
            case DesktopIconOutcome.Restored:
                ShowStatus(result.Placed == 1 ? Loc.Instance["Status_IconsRestoredOne"] : Loc.Format("Status_IconsRestored", result.Placed));
                break;
            case DesktopIconOutcome.AutoArrange:
                ShowDetail(Loc.Instance["Status_IconsAutoArrange"], InfoKind.Error);
                break;
            default:
                ShowDetail(Loc.Instance["Status_IconsUnavailable"], InfoKind.Error);
                break;
        }
    }

    [RelayCommand]
    private void OpenDisplaySettings() => ShellFolders.OpenUrl("ms-settings:display", Log);

    /// <summary>Reads the displays itself – only when the catalog has not read them yet.</summary>
    private async Task RefreshPlansAsync()
    {
        try
        {
            ShowPlans(await Task.Run(() => _display.QueryAsync(CancellationToken.None)));
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Displays could not be read for the profile list");
        }
    }

    /// <summary>Plans every profile against the displays as read, for the list's status lines and the head.</summary>
    private void ShowPlans(DisplaySnapshot snapshot)
    {
        CurrentTopology = TopologyDisplays.From(ProfileEditing.CurrentArrangement(snapshot, [], _catalog.KnownDisplayNames));
        _plans.Clear();
        foreach (Profile profile in _catalog.Profiles)
        {
            _plans[profile.Id] = _planner.Plan(profile, snapshot);
        }

        UpdateStatuses();
        if (Editor is { } editor && _plans.TryGetValue(editor.Id, out TopologyPlan? plan))
        {
            editor.ShowPlan(plan);
        }
    }

    protected override void UpdateStatuses()
    {
        foreach (ProfileItem item in Items)
        {
            (StatusKind kind, string text) = StatusOf(item);
            item.SetStatus(kind, text);
        }

        UpdateHead();
    }

    /// <summary>"Active · Default", "Ready · Dash missing (optional)", "Blocked: Odyssey G93SC missing" (F2 phase 0).</summary>
    private (StatusKind Kind, string Text) StatusOf(ProfileItem item)
    {
        if (item.IsNew)
        {
            return (StatusKind.Neutral, Loc.Instance["List_Unsaved"]);
        }

        string suffix = item.IsDefault ? " · " + Loc.Instance["Profile_Default"] : string.Empty;
        if (item.IsActive)
        {
            return (StatusKind.Ok, Loc.Instance["Profile_Active"] + suffix);
        }

        if (!_plans.TryGetValue(item.Profile.Id, out TopologyPlan? plan))
        {
            return (StatusKind.Neutral, Loc.Instance["List_Checking"] + suffix);
        }

        if (plan.IsBlocked)
        {
            return (StatusKind.Error, Loc.Instance["List_BlockedShort"] + suffix);
        }

        if (plan.Missing.Count > 0)
        {
            return (StatusKind.Ok, Loc.Format("List_OptionalMissing", Names(plan.Missing)) + suffix);
        }

        return (StatusKind.Ok, Loc.Instance["List_Ready"] + suffix);
    }

    private static string Names(IEnumerable<MissingDisplay> missing) =>
        string.Join(", ", missing.Select(m => SwitchMessages.NameOf(m.Assignment)));

    protected override void UpdateHead()
    {
        ProfileItem? item = SelectedItem;
        IsSelectedActive = item is { IsNew: false, IsActive: true };
        IsSelectedDefault = item is { IsNew: false, IsDefault: true };
        SwitchIsPrimary = !IsSelectedActive && Editor is { IsDirty: false };
        SwitchLabel = Loc.Instance[IsSelectedActive ? "Profile_Reapply" : "Profile_Apply"];
        if (item is null || Editor is null)
        {
            HeadStatusKind = StatusKind.Neutral;
            HeadStatusText = string.Empty;
            BlockedMessage = null;
            CanSwitch = false;
            TestCommand.NotifyCanExecuteChanged();
            return;
        }

        bool blocked = _plans.TryGetValue(item.Profile.Id, out TopologyPlan? plan) && plan.IsBlocked;
        MissingDisplay[] missing = blocked ? plan!.Missing.Where(m => !m.Assignment.IsOptional).ToArray() : [];
        BlockedMessage = missing.Length > 0 && !item.IsActive ? Loc.Format("Detail_BlockedNames", Names(missing)) : null;
        CanSwitch = !IsBusy && !item.IsNew && !Editor.IsDirty && !blocked;
        TestCommand.NotifyCanExecuteChanged();

        if (IsBusy)
        {
            HeadStatusKind = StatusKind.Accent;
            HeadStatusText = Loc.Instance["Tray_Switching"];
            return;
        }

        (StatusKind kind, string text) = StatusOf(item);
        if (item.IsNew)
        {
            HeadStatusKind = kind;
            HeadStatusText = text;
            return;
        }

        if (Editor.IsDirty)
        {
            text = Loc.Instance["SaveBar_Unsaved"];
            kind = StatusKind.Neutral;
        }
        else if (BlockedMessage is not null)
        {
            text = missing.Length == 1 ? Loc.Instance["Head_MissingOne"] : Loc.Format("Head_MissingMany", missing.Length);
        }

        if (Editor.Hotkey is { } hotkey)
        {
            text += " · " + HotkeyFormat.Format(hotkey);
        }

        HeadStatusKind = kind;
        HeadStatusText = text;
    }
}
