using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>An entry of a master list (R-NAV-2): a saved profile or game, or the unsaved new one on top.</summary>
public interface IMasterItem
{
    Guid Id { get; }

    string Name { get; }

    /// <summary>Exists only in the detail so far.</summary>
    bool IsNew { get; }

    void SetStatus(StatusKind kind, string text);
}

/// <summary>The detail of the selected entry: seeing and editing are the same view (R-NAV-3).</summary>
public interface IDetailEditor : INotifyPropertyChanged, IDisposable
{
    Guid Id { get; }

    string Name { get; }

    bool IsNew { get; }

    bool IsDirty { get; }

    /// <summary>Why the last save failed; shown in the detail bar.</summary>
    string? ErrorMessage { get; }

    /// <summary>The URL that runs the entry from a shortcut, a Stream Deck or the command line.</summary>
    string CommandText { get; }

    /// <summary>False: a problem remains or the disk said no; the editor says why.</summary>
    Task<bool> SaveAsync();
}

/// <summary>
/// A page with a master list on the left and the selected entry's editor on the right (R-NAV-2): selection, the question
/// before unsaved changes are left (R-NAV-3), loading, saving and discarding, the list rebuilt from the catalog with the
/// selection kept, and the bars above the detail. The profiles and games pages add only their head, their status texts
/// and their own commands (v4 finding A-07).
/// </summary>
/// <remarks>
/// Unsaved changes are never replaced from outside – a display change, a switch, the end of a game session or a learned
/// process name all reload the catalog. If the entry changed on disk meanwhile, a bar offers to reload it; a clean editor
/// reloads by itself when its entry changed and stays as it is otherwise (v4 finding A-01).
/// </remarks>
public abstract partial class MasterDetailViewModel<TItem, TEditor> : ObservableObject
    where TItem : class, IMasterItem
    where TEditor : class, IDetailEditor
{
    private static readonly TimeSpan StatusDuration = TimeSpan.FromSeconds(3);

    private readonly UiThread _ui = new();
    private TItem? _newItem;
    private Guid? _selectAfterRebuild;
    private bool _reverting;
    private bool _saving;
    private bool _rebuildAfterSave;
    private bool _shown;
    private CancellationTokenSource? _statusTimer;

    protected MasterDetailViewModel(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        Log = log;
    }

    /// <summary>
    /// The page is on screen for the first time: the selected entry gets its editor now. Until then the list is there but
    /// no editor – building one reads audio and USB devices and Surround, and nobody looks at it while the window shows
    /// another page or RigShift starts into the tray (v4 finding A-06).
    /// </summary>
    public void PageShown()
    {
        if (_shown)
        {
            return;
        }

        _shown = true;
        if (Editor is null && SelectedItem is { } item)
        {
            _ = LoadEditorAsync(item);
        }
    }

    /// <summary>The detail wants the keyboard focus in the name field: a new entry is named first.</summary>
    public event EventHandler? FocusNameRequested;

    /// <summary>The saved entries, with the unsaved new one on top while there is one.</summary>
    public ObservableCollection<TItem> Items { get; } = [];

    [ObservableProperty]
    public partial TItem? SelectedItem { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial TEditor? Editor { get; private set; }

    public bool HasSelection => Editor is not null;

    /// <summary>No entries at all: the list says so and the detail stays empty.</summary>
    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    /// <summary>The entry changed on disk while its editor holds unsaved changes; a bar offers to reload it.</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; }

    /// <summary>A result or a store error, as a bar above the tab content; closable.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetailMessage))]
    public partial string? DetailMessage { get; private set; }

    public bool HasDetailMessage => DetailMessage is not null;

    [ObservableProperty]
    public partial InfoKind DetailKind { get; private set; }

    /// <summary>"'X' saved", for three seconds at the bottom of the detail.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusMessage))]
    public partial string? StatusMessage { get; private set; }

    public bool HasStatusMessage => StatusMessage is not null;

    protected ILogger Log { get; }

    /// <summary>The saved entries as the catalog has them, in list order.</summary>
    protected abstract IEnumerable<TItem> CatalogItems { get; }

    /// <summary>"New profile", "New game": the name in the unsaved question while the entry has none yet.</summary>
    protected abstract string UnnamedText { get; }

    /// <summary>Before the page is left or the window navigates: saves, discards or stays. False: stay.</summary>
    public Task<bool> ConfirmLeaveAsync() => ConfirmLeaveAsync(null);

    protected abstract Task<TEditor> CreateEditorAsync(TItem item);

    protected abstract Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? target);

    protected abstract Task<bool> ConfirmDeleteAsync(TItem item);

    protected abstract Task DeleteStoredAsync(TItem item);

    /// <summary>Whether the entry under <paramref name="editor"/> changed on disk since the editor opened or last saved.</summary>
    protected abstract bool StoredChanged(TEditor editor);

    /// <summary>The status lines of the list; ends with <see cref="UpdateHead"/>.</summary>
    protected abstract void UpdateStatuses();

    /// <summary>The head's status line and what the primary action may do right now.</summary>
    protected abstract void UpdateHead();

    /// <summary>A new editor is bound: page extras such as the plan or the tab to open.</summary>
    protected virtual void OnEditorLoaded(TEditor editor, TItem item)
    {
    }

    /// <summary>Another editor (or none) is bound.</summary>
    protected virtual void OnEditorReplaced()
    {
    }

    /// <summary>A property of the bound editor changed, beyond what the base follows.</summary>
    protected virtual void OnEditorPropertyChanged(TEditor editor, string? propertyName)
    {
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

    /// <summary>The saved version replaces the unsaved changes (the "changed elsewhere" bar).</summary>
    [RelayCommand]
    private Task ReloadAsync()
    {
        Log.Information("Editor of {Name} reloaded from disk on request, unsaved changes dropped", Editor?.Name);
        return LoadEditorAsync(SelectedItem);
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedItem is not { } item || !await ConfirmDeleteAsync(item))
        {
            return;
        }

        if (item.IsNew)
        {
            DiscardCore();
            return;
        }

        int index = Items.IndexOf(item);
        _selectAfterRebuild = Items.ElementAtOrDefault(index + 1)?.Id ?? Items.ElementAtOrDefault(index - 1)?.Id;
        CloseEditor();
        await RunStoreActionAsync(() => DeleteStoredAsync(item), Loc.Format("Status_Deleted", item.Name));
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
            Log.Warning(ex, "Clipboard refused the command");
        }
    }

    /// <summary>A new entry at the top of the list, selected; it stays there unsaved until the first save.</summary>
    protected void BeginNew(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _newItem = item;
        item.SetStatus(StatusKind.Neutral, Loc.Instance["List_Unsaved"]);
        Items.Insert(0, item);
        IsEmpty = false;
        SelectedItem = item;
    }

    /// <summary>The entry to select after the next rebuild, e.g. one just added to the catalog.</summary>
    protected void SelectAfterRebuild(Guid id) => _selectAfterRebuild = id;

    /// <summary>
    /// The list from the catalog, the new entry on top; the selection survives by id. An editor with unsaved changes is
    /// never replaced and keeps its entry selected; a clean one reloads only when its entry changed on disk.
    /// </summary>
    protected void Rebuild()
    {
        // The save reloads the catalog before it is done; the list follows once it is, see SaveCoreAsync.
        if (_saving)
        {
            _rebuildAfterSave = true;
            return;
        }

        Guid? keep = _selectAfterRebuild ?? SelectedItem?.Id;
        _selectAfterRebuild = null;
        _reverting = true;
        try
        {
            // A new entry is an ordinary one once the catalog has it.
            if (_newItem is not null && CatalogItems.Any(i => i.Id == _newItem.Id))
            {
                _newItem = null;
            }

            Items.Clear();
            if (_newItem is not null)
            {
                Items.Add(_newItem);
            }

            foreach (TItem item in CatalogItems)
            {
                Items.Add(item);
            }

            IsEmpty = Items.Count == 0;
            UpdateStatuses();

            if (Editor is { IsDirty: true } dirty && Items.FirstOrDefault(i => i.Id == dirty.Id) is { } editing)
            {
                SelectedItem = editing;
                IsStale = !editing.IsNew && StoredChanged(dirty);
                if (IsStale)
                {
                    Log.Information("{Name} changed on disk while its editor holds unsaved changes; offering a reload", dirty.Name);
                }

                UpdateHead();
                return;
            }

            if (Editor is { IsDirty: true } lost)
            {
                Log.Warning("{Name} is gone from disk; its unsaved changes are dropped", lost.Name);
            }

            TItem? selected = Items.FirstOrDefault(i => i.Id == keep) ?? Items.FirstOrDefault();
            bool keepEditor = selected is { IsNew: false } && Editor is { } editor && editor.Id == selected.Id && !StoredChanged(editor);
            SelectedItem = selected;
            if (keepEditor)
            {
                IsStale = false;
                UpdateHead();
            }
            else
            {
                _ = LoadEditorAsync(selected);
            }
        }
        finally
        {
            _reverting = false;
        }
    }

    /// <summary>
    /// The entry may have changed on disk without the list changing (a profile's USB rules live in the settings): a clean
    /// editor reloads, one with unsaved changes gets the bar.
    /// </summary>
    protected void RecheckEditor()
    {
        if (_saving || Editor is not { } editor || SelectedItem is not { IsNew: false } item || item.Id != editor.Id)
        {
            return;
        }

        if (!StoredChanged(editor))
        {
            IsStale = false;
        }
        else if (editor.IsDirty)
        {
            IsStale = true;
        }
        else
        {
            _ = LoadEditorAsync(item);
        }
    }

    protected async Task RunStoreActionAsync(Func<Task> action, string? success)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            await action();
            if (success is not null)
            {
                ShowStatus(success);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Error(ex, "Store action failed");
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
        }
    }

    protected virtual void ShowDetail(string text, InfoKind kind)
    {
        DetailKind = kind;
        DetailMessage = text;
    }

    protected void ShowStatus(string text)
    {
        _statusTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _statusTimer = timer;
        StatusMessage = text;
        _ = HideStatusAsync(timer);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread: right away when already there.</summary>
    protected void OnUi(Action action) => _ui.Run(action);

    partial void OnSelectedItemChanged(TItem? oldValue, TItem? newValue)
    {
        if (!_reverting)
        {
            _ = SelectAsync(oldValue, newValue);
        }
    }

    partial void OnEditorChanged(TEditor? value) => OnEditorReplaced();

    /// <param name="target">The entry the user picked instead, when the question comes from the list.</param>
    private async Task<bool> ConfirmLeaveAsync(TItem? target)
    {
        if (Editor is not { IsDirty: true } editor)
        {
            return true;
        }

        switch (await ConfirmUnsavedAsync(editor.Name.Trim().Length == 0 ? UnnamedText : editor.Name, target?.Name))
        {
            case UnsavedChoice.Save:
                // The editor asked about, not whatever is bound once the question is answered.
                return await SaveCoreAsync(target, editor);
            case UnsavedChoice.Discard:
                DiscardCore(target);
                return true;
            default:
                return false;
        }
    }

    private async Task SelectAsync(TItem? previous, TItem? next)
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

    private async Task LoadEditorAsync(TItem? item)
    {
        IsStale = false;
        DetailMessage = null;
        if (item is null)
        {
            CloseEditor();
            UpdateHead();
            return;
        }

        if (!_shown)
        {
            // PageShown loads it.
            UpdateHead();
            return;
        }

        // The old editor stays bound until the new one is ready: an empty detail in between would flash.
        TEditor editor;
        try
        {
            editor = await CreateEditorAsync(item);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception or COMException)
        {
            // Not the old editor either: the list shows another entry now (v4 finding A-16).
            Log.Error(ex, "The editor of {Name} could not be opened", item.Name);
            CloseEditor();
            ShowDetail(Loc.Format("Status_Error", ex.Message), InfoKind.Error);
            UpdateHead();
            return;
        }

        if (SelectedItem != item)
        {
            editor.Dispose();
            return;
        }

        CloseEditor();
        editor.PropertyChanged += OnEditorPropertyChanged;
        Editor = editor;
        OnEditorLoaded(editor, item);
        UpdateHead();
        if (item.IsNew)
        {
            FocusNameRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Unbinds and disposes the editor; the detail is empty until the next one is loaded.</summary>
    private void CloseEditor()
    {
        IsStale = false;
        if (Editor is not { } editor)
        {
            return;
        }

        editor.PropertyChanged -= OnEditorPropertyChanged;
        Editor = null;
        editor.Dispose();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TEditor editor || editor != Editor)
        {
            return;
        }

        if (e.PropertyName is nameof(IDetailEditor.IsDirty) or nameof(IDetailEditor.IsNew) or nameof(IDetailEditor.Name)
            or nameof(IDetailEditor.ErrorMessage))
        {
            if (e.PropertyName == nameof(IDetailEditor.ErrorMessage) && editor.ErrorMessage is { } error)
            {
                ShowDetail(error, InfoKind.Error);
            }

            UpdateHead();
        }

        OnEditorPropertyChanged(editor, e.PropertyName);
    }

    /// <param name="target">The entry to show afterwards; otherwise the list stays on the saved one.</param>
    /// <param name="editor">The editor to save; the bound one when omitted.</param>
    private async Task<bool> SaveCoreAsync(TItem? target = null, TEditor? editor = null)
    {
        editor ??= Editor;
        if (editor is null)
        {
            return true;
        }

        bool wasNew = editor.IsNew;
        bool saved;
        _saving = true;
        try
        {
            saved = await editor.SaveAsync();
        }
        finally
        {
            _saving = false;
        }

        bool rebuild = _rebuildAfterSave;
        _rebuildAfterSave = false;
        if (!saved)
        {
            if (rebuild)
            {
                Rebuild();
            }

            return false;
        }

        if (wasNew)
        {
            _newItem = null;
        }

        _selectAfterRebuild = target?.Id ?? editor.Id;
        Rebuild();
        return true;
    }

    /// <summary>Back to the entry as saved; a new entry disappears from the list.</summary>
    /// <param name="target">The entry the user picked instead; its editor is loaded by whoever asked.</param>
    private void DiscardCore(TItem? target = null)
    {
        TItem? item = _newItem is not null && Editor is { IsNew: true } ? _newItem : SelectedItem;
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
}
