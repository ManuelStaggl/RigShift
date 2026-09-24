using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>Picks a program for an app entry; a window in the app, a script in tests.</summary>
public interface IAppPicker
{
    /// <summary>The program picked, or <c>null</c> when the user cancelled.</summary>
    /// <param name="currentPath">The entry's program, to start from; <c>null</c> for a new entry.</param>
    Views.PickedApp? Pick(string? currentPath);
}

/// <summary>
/// The programs a profile or a game starts and ends, and the USB device they wait for – one list, one card template
/// (<c>Resources/EditorTemplates.xaml</c>) for both editors (v4 finding A-07).
/// </summary>
public sealed partial class AppListEditor : ObservableObject, IDisposable
{
    private readonly IAppPicker _picker;
    private readonly string _addTextKey;

    /// <param name="showWhen">Offer "before / after the game": only a game has a game to be before or after.</param>
    /// <param name="addTextKey">The add button's text.</param>
    public AppListEditor(IEnumerable<AppAction> apps, bool showWhen, AppsWaitDeviceChoice waitDevice, IAppPicker picker, string addTextKey)
    {
        ArgumentNullException.ThrowIfNull(apps);
        ArgumentNullException.ThrowIfNull(waitDevice);
        ArgumentNullException.ThrowIfNull(picker);
        _picker = picker;
        _addTextKey = addTextKey;
        ShowWhen = showWhen;
        WaitDevice = waitDevice;
        foreach (AppAction app in apps)
        {
            var item = new AppEditItem(app, showWhen);
            item.PropertyChanged += OnItemChanged;
            Items.Add(item);
        }

        Items.CollectionChanged += OnItemsChanged;
        waitDevice.PropertyChanged += OnWaitDeviceChanged;
    }

    /// <summary>Raised on every change to the list, an entry or the device.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<AppEditItem> Items { get; } = [];

    public bool ShowWhen { get; }

    public AppsWaitDeviceChoice WaitDevice { get; }

    public string AddText => Loc.Instance[_addTextKey];

    public IReadOnlyList<AppAction> Build() => [.. Items.Select(a => a.ToAction())];

    /// <summary>Adds an entry for a program that is already known, e.g. from a template.</summary>
    public void Add(string path, string? name = null) =>
        Items.Add(new AppEditItem(new AppAction { Path = path, Name = name }, ShowWhen));

    /// <summary>New texts after a language change, same entries.</summary>
    public void Relabel()
    {
        foreach (AppEditItem item in Items)
        {
            item.Relabel();
        }

        WaitDevice.Relabel();
        OnPropertyChanged(nameof(AddText));
    }

    public void Dispose()
    {
        Items.CollectionChanged -= OnItemsChanged;
        WaitDevice.PropertyChanged -= OnWaitDeviceChanged;
        foreach (AppEditItem item in Items)
        {
            item.PropertyChanged -= OnItemChanged;
        }
    }

    /// <summary>A new entry starts with the picker; cancelling it adds nothing (finding HW-11).</summary>
    [RelayCommand]
    private void AddPicked()
    {
        if (_picker.Pick(null) is { } picked)
        {
            Add(picked.Path, picked.Name);
        }
    }

    [RelayCommand]
    private void Browse(AppEditItem? item)
    {
        if (item is not null && _picker.Pick(item.Path) is { } picked)
        {
            item.SetPicked(picked.Path, picked.Name);
        }
    }

    [RelayCommand]
    private void Remove(AppEditItem? item)
    {
        if (item is not null)
        {
            Items.Remove(item);
        }
    }

    [RelayCommand]
    private void MoveUp(AppEditItem? item) => Move(item, -1);

    [RelayCommand]
    private void MoveDown(AppEditItem? item) => Move(item, 1);

    private void Move(AppEditItem? item, int offset)
    {
        int index = item is null ? -1 : Items.IndexOf(item);
        int target = index + offset;
        if (index >= 0 && target >= 0 && target < Items.Count)
        {
            Items.Move(index, target);
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (AppEditItem item in e.OldItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged -= OnItemChanged;
        }

        foreach (AppEditItem item in e.NewItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged += OnItemChanged;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e) => Changed?.Invoke(this, EventArgs.Empty);

    private void OnWaitDeviceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppsWaitDeviceChoice.Selected))
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>
/// "Wait for USB device" above an app list: "Don't wait", the connected devices, then the known ones that are not
/// connected – the saved device among them, with its name. The same for profiles and games (v4 finding A-07).
/// </summary>
public sealed partial class AppsWaitDeviceChoice : ObservableObject
{
    private readonly IReadOnlyList<UsbDevice> _connected;
    private readonly IReadOnlyList<RuleDevice> _known;
    private readonly IReadOnlyDictionary<string, string>? _customNames;
    private readonly Dictionary<string, string> _windowsNames = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="savedId">The device the profile or game waits for now; offered even while it is not connected.</param>
    /// <param name="known">Devices RigShift knows from rules, profiles and games.</param>
    public AppsWaitDeviceChoice(
        string? savedId,
        string? savedName,
        IReadOnlyList<UsbDevice> connected,
        IEnumerable<RuleDevice> known,
        IReadOnlyDictionary<string, string>? customNames)
    {
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(known);
        _connected = connected;
        _customNames = customNames;
        string? saved = UsbDeviceIds.Normalize(savedId);
        _known = [.. saved is null ? [] : new[] { new RuleDevice { Id = saved, Name = savedName } }, .. known];
        Fill(saved);
    }

    public ObservableCollection<Choice> Choices { get; } = [];

    [ObservableProperty]
    public partial Choice? Selected { get; set; }

    /// <summary>The device to wait for; <c>null</c> for "don't wait".</summary>
    public string? DeviceId => Selected?.Key;

    /// <summary>Windows' name of the device, stored with the id so a disconnected device still reads as a name.</summary>
    public string? DeviceName => Selected?.Key is { } id && _windowsNames.TryGetValue(id, out string? name) ? name : null;

    /// <summary>New texts after a language change, same device.</summary>
    public void Relabel() => Fill(Selected?.Key);

    private void Fill(string? selectedKey)
    {
        UsbDeviceChoices.Fill(Choices, _windowsNames, _connected, _known, _customNames);
        Choices.Insert(0, new Choice(null, Loc.Instance["Editor_AppsWaitNone"]));
        Selected = Choices.FirstOrDefault(c => string.Equals(c.Key, selectedKey, StringComparison.OrdinalIgnoreCase)) ?? Choices[0];
    }
}
