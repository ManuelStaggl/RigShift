using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The profiles page: the master list on the left, the selected profile's detail (its editor) on the right (R-NAV-2).
/// Leaving a profile with unsaved changes asks first (R-NAV-3); the head shows what a switch would do right now (F2 phase 0).
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(3);

    private readonly ProfileCatalog _catalog;
    private readonly SwitchCoordinator _coordinator;
    private readonly IProfilePageDialogs _dialogs;
    private readonly SettingsService _settings;
    private readonly IDisplayConfigurator _display;
    private readonly TopologyPlanner _planner;
    private readonly ILogger _log;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private readonly Dictionary<Guid, TopologyPlan> _plans = [];
    private ProfileItem? _newItem;
    private Guid? _selectAfterRebuild;
    private bool _rebuildQueued;
    private bool _reverting;
    private CancellationTokenSource? _statusTimer;

    public ProfilesViewModel(
        ProfileCatalog catalog,
        SwitchCoordinator coordinator,
        IProfilePageDialogs dialogs,
        SettingsService settings,
        IDisplayConfigurator display,
        TopologyPlanner planner,
        DisplayChangeWatcher watcher,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(watcher);
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _coordinator = coordinator;
        _dialogs = dialogs;
        _settings = settings;
        _display = display;
        _planner = planner;
        _log = log.ForContext<ProfilesViewModel>();

        catalog.Changed += (_, _) => OnUi(() => _ = RebuildAsync());
        catalog.Items.CollectionChanged += OnCatalogItemsChanged;
        settings.Changed += (_, _) => OnUi(UpdateStatuses);
        watcher.DisplaysChanged += (_, _) => OnUi(() => _ = RefreshPlansAsync());
        coordinator.SwitchCompleted += (_, _) => OnUi(() => _ = RefreshPlansAsync());
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
        _ = RefreshPlansAsync();
    }

    /// <summary>The detail wants the keyboard focus in the name field: a new profile is named first (F3 step 1).</summary>
    public event EventHandler? FocusNameRequested;

    public ProfileCatalog Catalog => _catalog;

    /// <summary>The saved profiles, with the unsaved new one on top while there is one.</summary>
    public ObservableCollection<ProfileItem> Items { get; } = [];

    [ObservableProperty]
    public partial ProfileItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial ProfileEditorViewModel? Editor { get; private set; }

    public bool HasSelection => Editor is not null;

    /// <summary>No profiles at all: the list says so and the detail stays empty (section 6).</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

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

    /// <summary>A test result, a switch result or a store error, as a bar above the tab content; closable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailMessage))]
    public partial string? DetailMessage { get; private set; }

    public bool HasDetailMessage => DetailMessage is not null;

    [ObservableProperty]
    public partial InfoKind DetailKind { get; private set; }

    /// <summary>"'X' saved", for three seconds at the bottom of the detail (F3 step 4).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasStatusMessage => StatusMessage is not null;

    /// <summary>Before the page is left or the window navigates: saves, discards or stays. False: stay.</summary>
    public Task<bool> ConfirmLeaveAsync() => ConfirmLeaveAsync(null);

    /// <param name="target">The profile the user picked instead, when the question comes from the list.</param>
    private async Task<bool> ConfirmLeaveAsync(ProfileItem? target)
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        switch (await _dialogs.ConfirmUnsavedAsync(
            editor.Name.Trim().Length == 0 ? Loc.Instance["Editor_NewName"] : editor.Name, target?.Name))
        {
            case UnsavedChoice.Save:
                return await SaveCoreAsync(target);
            case UnsavedChoice.Discard:
                DiscardCore(target);
                return true;
            default:
                return false;
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
        ShowDetail(title + " " + text, record.Outcome switch
        {
            SwitchOutcome.Applied => InfoKind.Info,
            SwitchOutcome.Failed => InfoKind.Error,
            _ => InfoKind.Warn,
        });
    }

    [RelayCommand]
    private async Task NewFromCurrentAsync()
    {
        if (!await ConfirmLeaveAsync())
        {
            return;
        }

        Profile profile = await _dialogs.NewFromCurrentAsync();
        _newItem = new ProfileItem(profile, _settings.Current.UsbDeviceNames) { IsNew = true };
        _newItem.SetStatus(StatusKind.Neutral, Loc.Instance["List_Unsaved"]);
        Items.Insert(0, _newItem);
        IsEmpty = false;
        SelectedItem = _newItem;
        _log.Information("New profile started from the current arrangement with {Count} displays", profile.Displays.Count);
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

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (await SaveCoreAsync())
        {
            ShowStatus(Loc.Format("Status_Saved", Editor?.Name ?? string.Empty));
        }
    }

    [RelayCommand]
    private void Discard() => DiscardCore();

    [RelayCommand]
    private void CloseDetailMessage() => DetailMessage = null;

    [RelayCommand]
    private async Task DuplicateAsync()
    {
        if (SelectedItem is not { IsNew: false } item)
        {
            return;
        }

        Profile copy = item.Profile with
        {
            Id = Guid.NewGuid(),
            Name = ProfileEditing.UniqueName(Loc.Format("Profile_CopyName", item.Name), _catalog.Profiles.Select(p => p.Name)),
        };
        _selectAfterRebuild = copy.Id;
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
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item || !await _dialogs.ConfirmDeleteAsync(item.Name, _catalog.RuleCount(item.Profile.Id)))
        {
            return;
        }

        if (item.IsNew)
        {
            DiscardCore();
            return;
        }

        int index = Items.IndexOf(item);
        _selectAfterRebuild = Items.ElementAtOrDefault(index + 1)?.Profile.Id ?? Items.ElementAtOrDefault(index - 1)?.Profile.Id;
        Editor?.Dispose();
        Editor = null;
        await RunStoreActionAsync(() => _catalog.DeleteAsync(item.Profile, CancellationToken.None), Loc.Format("Status_Deleted", item.Name));
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
            _log.Information("Shortcut {File} created for profile {Profile}", file, item.Name);
            ShowStatus(Loc.Format("Status_ShortcutCreated", title));
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            _log.Error(ex, "Shortcut {File} could not be created", file);
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    [RelayCommand]
    private void CopyCommand()
    {
        if (Editor is null)
        {
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(Editor.CommandText);
            ShowStatus(Loc.Instance["Trigger_Copied"]);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Clipboard refused the command");
        }
    }

    [RelayCommand]
    private void OpenDisplaySettings() => ShellFolders.OpenUrl("ms-settings:display", _log);

    partial void OnSelectedItemChanged(ProfileItem? oldValue, ProfileItem? newValue)
    {
        if (!_reverting)
        {
            _ = SelectAsync(oldValue, newValue);
        }
    }

    private async Task SelectAsync(ProfileItem? previous, ProfileItem? next)
    {
        if (previous is not null && previous != next && Editor is { IsDirty: true } && !await ConfirmLeaveAsync(next))
        {
            _reverting = true;
            SelectedItem = previous;
            _reverting = false;
            return;
        }

        // Confirming may have removed a new item or replaced the list; the selection then is whatever the list shows.
        if (SelectedItem != next)
        {
            return;
        }

        await LoadEditorAsync(next);
    }

    private async Task LoadEditorAsync(ProfileItem? item)
    {
        Editor?.Dispose();
        DetailMessage = null;
        if (item is null)
        {
            Editor = null;
            UpdateHead();
            return;
        }

        ProfileEditorViewModel editor = await _dialogs.CreateEditorAsync(item.Profile, item.IsNew);
        if (SelectedItem != item)
        {
            editor.Dispose();
            return;
        }

        editor.PropertyChanged += OnEditorChanged;
        Editor = editor;
        if (_plans.TryGetValue(item.Profile.Id, out TopologyPlan? plan))
        {
            editor.ShowPlan(plan);
        }

        UpdateHead();
        if (item.IsNew)
        {
            FocusNameRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void OnEditorChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ProfileEditorViewModel.IsDirty) or nameof(ProfileEditorViewModel.IsNew) or nameof(ProfileEditorViewModel.Name)
            or nameof(ProfileEditorViewModel.ErrorMessage))
        {
            if (e.PropertyName == nameof(ProfileEditorViewModel.ErrorMessage) && Editor?.ErrorMessage is { } error)
            {
                ShowDetail(error, InfoKind.Error);
            }

            UpdateHead();
        }
    }

    /// <param name="target">The profile to show afterwards; saving rebuilds the list, which otherwise stays on the saved profile.</param>
    private async Task<bool> SaveCoreAsync(ProfileItem? target = null)
    {
        if (Editor is not { } editor)
        {
            return true;
        }

        bool wasNew = editor.IsNew;
        _selectAfterRebuild = target?.Profile.Id ?? editor.Id;
        if (!await editor.SaveAsync())
        {
            _selectAfterRebuild = null;
            return false;
        }

        if (wasNew)
        {
            _newItem = null;
        }

        // The catalog reloads and fires Changed, which rebuilds the list and keeps this profile selected.
        return true;
    }

    /// <summary>Back to the profile as saved; a new profile disappears from the list.</summary>
    /// <param name="target">The profile the user picked instead; its editor is loaded by whoever asked.</param>
    private void DiscardCore(ProfileItem? target = null)
    {
        ProfileItem? item = _newItem is not null && Editor is { IsNew: true } ? _newItem : SelectedItem;
        if (item is null)
        {
            return;
        }

        if (item.IsNew)
        {
            _newItem = null;
            Items.Remove(item);
            IsEmpty = Items.Count == 0;
            _reverting = true;
            SelectedItem = target is not null && Items.Contains(target) ? target : Items.FirstOrDefault();
            _reverting = false;
            if (target is null)
            {
                _ = LoadEditorAsync(SelectedItem);
            }

            return;
        }

        if (target is null)
        {
            _ = LoadEditorAsync(item);
        }
    }

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
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    private void OnCatalogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // The catalog clears and refills its list item by item; one rebuild after the burst is enough.
        if (_rebuildQueued)
        {
            return;
        }

        _rebuildQueued = true;
        OnUi(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private async Task RebuildAsync()
    {
        Rebuild();
        await RefreshPlansAsync();
    }

    /// <summary>The list from the catalog, the new profile on top; the selection survives by id.</summary>
    private void Rebuild()
    {
        Guid? keep = _selectAfterRebuild ?? SelectedItem?.Profile.Id;
        _selectAfterRebuild = null;
        _reverting = true;
        try
        {
            // The save of a new profile reloads the catalog before it returns here: by then the profile is an ordinary entry.
            if (_newItem is not null && _catalog.Items.Any(i => i.Profile.Id == _newItem.Profile.Id))
            {
                _newItem = null;
            }

            Items.Clear();
            if (_newItem is not null)
            {
                Items.Add(_newItem);
            }

            foreach (ProfileItem item in _catalog.Items)
            {
                Items.Add(item);
            }

            IsEmpty = Items.Count == 0;
            UpdateStatuses();
            ProfileItem? selected = Items.FirstOrDefault(i => i.Profile.Id == keep) ?? Items.FirstOrDefault();
            bool sameProfile = selected is not null && Editor?.Id == selected.Profile.Id && !selected.IsNew && Editor is { IsDirty: false };
            SelectedItem = selected;
            if (!sameProfile || Editor is null)
            {
                _ = LoadEditorAsync(selected);
            }
            else
            {
                UpdateHead();
            }
        }
        finally
        {
            _reverting = false;
        }
    }

    /// <summary>Plans every profile against the live displays once, for the list's status lines and the head.</summary>
    private async Task RefreshPlansAsync()
    {
        DisplaySnapshot snapshot;
        try
        {
            snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Displays could not be read for the profile list");
            return;
        }

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

    private void UpdateStatuses()
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

    /// <summary>The head's status line and what the primary action may do right now.</summary>
    private void UpdateHead()
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

    private void ShowDetail(string text, InfoKind kind)
    {
        DetailKind = kind;
        DetailMessage = text;
    }

    private void ShowStatus(string text)
    {
        _statusTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _statusTimer = timer;
        StatusMessage = text;
        _ = HideStatusAsync(timer);
    }

    private async Task HideStatusAsync(CancellationTokenSource timer)
    {
        try
        {
            await Task.Delay(StatusDuration, timer.Token);
            StatusMessage = null;
        }
        catch (OperationCanceledException)
        {
            // A newer message took over.
        }
        finally
        {
            if (_statusTimer == timer)
            {
                _statusTimer = null;
            }

            timer.Dispose();
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
