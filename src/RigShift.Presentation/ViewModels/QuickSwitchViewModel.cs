using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;

namespace RigShift.App.ViewModels;

/// <summary>One profile or game in the quick switcher.</summary>
public sealed record QuickSwitchItem(string Name, string Kind, ProfileItem? Profile, GameItem? Game);

/// <summary>
/// The quick switcher: profiles and games in one list, filtered by typing, run with Enter. Runs through the tray popup's
/// commands, so a switch or a game start from here behaves exactly like one from the tray.
/// </summary>
public sealed partial class QuickSwitchViewModel(TrayPopupViewModel tray) : ObservableObject
{
    private List<QuickSwitchItem> _all = [];

    public ObservableCollection<QuickSwitchItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial QuickSwitchItem? Selected { get; set; }

    /// <summary>Raised once a profile or game was run, so the window goes away.</summary>
    public event EventHandler? Done;

    /// <summary>Takes the profiles and games as they are now and starts with an empty filter.</summary>
    public void Refresh()
    {
        string profile = Loc.Instance["QuickSwitch_Profile"];
        string game = Loc.Instance["QuickSwitch_Game"];
        _all = [
            .. tray.Catalog.Items.Where(p => !p.IsNew).Select(p => new QuickSwitchItem(p.Name, profile, p, null)),
            .. tray.Games.Items.Where(g => !g.IsNew).Select(g => new QuickSwitchItem(g.Name, game, null, g))];
        Query = string.Empty;
        Filter();
    }

    /// <summary>Up and down in the filtered list, wrapping at the ends.</summary>
    public void Move(int step)
    {
        if (Items.Count == 0)
        {
            return;
        }

        int index = Selected is null ? -1 : Items.IndexOf(Selected);
        Selected = Items[((index + step) % Items.Count + Items.Count) % Items.Count];
    }

    /// <summary>Switches to the selected profile or starts the selected game; false when nothing is selected.</summary>
    public bool Run()
    {
        if (Selected is not { } item)
        {
            return false;
        }

        if (item.Profile is { } profile)
        {
            tray.SwitchCommand.Execute(profile);
        }
        else
        {
            tray.PlayCommand.Execute(item.Game);
        }

        Done?.Invoke(this, EventArgs.Empty);
        return true;
    }

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        string query = Query.Trim();
        Items.Clear();
        foreach (QuickSwitchItem item in _all.Where(i => query.Length == 0 || i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)))
        {
            Items.Add(item);
        }

        Selected = Items.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }
}

/// <summary>The quick switcher's hotkey on the settings page; saved at once, like the toggle hotkey.</summary>
public sealed partial class QuickSwitchHotkeyField : ObservableObject, IHotkeyField
{
    private readonly HotkeyRecorder _recorder;
    private readonly Action<Func<AppSettings, AppSettings>> _persist;

    public QuickSwitchHotkeyField(HotkeyService hotkeys, Action<Func<AppSettings, AppSettings>> persist)
    {
        _recorder = new HotkeyRecorder(hotkeys, HotkeyUseKind.QuickSwitch, Guid.Empty, "Settings_QuickSwitchHotkeyHint");
        _persist = persist;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Text), nameof(HasHotkey))]
    public partial Hotkey? Hotkey { get; set; }

    public string Text => Hotkey is null ? string.Empty : HotkeyFormat.Format(Hotkey);

    public bool HasHotkey => Hotkey is not null;

    public string Hint => _recorder.Hint;

    public void BeginHotkeyRecording() => _recorder.Begin();

    public void EndHotkeyRecording() => _recorder.End();

    public void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        if (_recorder.Record(modifiers, virtualKey) is { } hotkey)
        {
            Hotkey = hotkey;
            _persist(s => s with { QuickSwitchHotkey = hotkey });
        }

        OnPropertyChanged(nameof(Hint));
    }

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void ClearHotkey()
    {
        Hotkey = null;
        _recorder.Reset();
        OnPropertyChanged(nameof(Hint));
        _persist(s => s with { QuickSwitchHotkey = null });
    }

    /// <summary>New texts after a language change.</summary>
    public void Relabel()
    {
        OnPropertyChanged(nameof(Hint));
        OnPropertyChanged(nameof(Text));
    }
}
