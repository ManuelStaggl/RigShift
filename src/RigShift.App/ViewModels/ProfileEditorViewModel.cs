using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// Editor for one profile: name, icon, confirmation time, which displays take part (primary, optional) and audio.
/// Modes and positions are not editable; they come from "use current arrangement" (docs/PLAN.md, section 10, M4).
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject
{
    private readonly Profile _original;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly HotkeyService _hotkeys;
    private readonly ILogger _log;

    public ProfileEditorViewModel(
        Profile profile,
        bool isNew,
        IReadOnlyList<AudioDeviceInfo> playbackDevices,
        IReadOnlyList<AudioDeviceInfo> recordingDevices,
        int appConfirmTimeoutSeconds,
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        HotkeyService hotkeys,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(log);

        _original = profile;
        _catalog = catalog;
        _display = display;
        _hotkeys = hotkeys;
        Hotkey = profile.Hotkey;
        HotkeyHint = Loc.Instance["Editor_HotkeyHint"];
        _log = log.ForContext<ProfileEditorViewModel>();

        Title = Loc.Instance[isNew ? "Editor_TitleNew" : "Editor_TitleEdit"];
        TimeoutHint = Loc.Format("Editor_OwnTimeoutHint", appConfirmTimeoutSeconds);
        Name = profile.Name;
        IconChoices = [.. ProfileIcons.All.Select(key => new Choice(key, Loc.Instance["Icon_" + char.ToUpperInvariant(key[0]) + key[1..]]))];
        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == ProfileIcons.Normalize(profile.Icon))
            ?? IconChoices.First(c => c.Key == ProfileIcons.Rig);
        UseOwnTimeout = profile.ConfirmTimeoutSeconds is not null;
        OwnTimeoutSeconds = profile.ConfirmTimeoutSeconds ?? appConfirmTimeoutSeconds;
        SetDisplays(profile.Displays);

        AudioAssignment audio = profile.Audio;
        AudioSlots =
        [
            new AudioSlot(Loc.Instance["Audio_Playback"], Loc.Instance["Audio_Unchanged"], playbackDevices, audio.Playback),
            new AudioSlot(Loc.Instance["Audio_PlaybackComms"], Loc.Instance["Audio_SameAsAbove"], playbackDevices, audio.PlaybackCommunications),
            new AudioSlot(Loc.Instance["Audio_Recording"], Loc.Instance["Audio_Unchanged"], recordingDevices, audio.Recording),
            new AudioSlot(Loc.Instance["Audio_RecordingComms"], Loc.Instance["Audio_SameAsAbove"], recordingDevices, audio.RecordingCommunications),
        ];
    }

    /// <summary>True: saved, close the window. False: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    public string TimeoutHint { get; }

    public ObservableCollection<Choice> IconChoices { get; }

    public ObservableCollection<DisplayEditItem> Displays { get; } = [];

    public IReadOnlyList<AudioSlot> AudioSlots { get; }

    /// <summary>The profile as saved, after <see cref="CloseRequested"/> with <c>true</c>.</summary>
    public Profile? Saved { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedIcon { get; set; }

    [ObservableProperty]
    public partial bool UseOwnTimeout { get; set; }

    [ObservableProperty]
    public partial double? OwnTimeoutSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HotkeyText), nameof(HasHotkey))]
    public partial Hotkey? Hotkey { get; set; }

    public string HotkeyText => Hotkey is null ? string.Empty : HotkeyFormat.Format(Hotkey);

    public bool HasHotkey => Hotkey is not null;

    [ObservableProperty]
    public partial string HotkeyHint { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblems))]
    public partial string? Problems { get; set; }

    public bool HasProblems => !string.IsNullOrEmpty(Problems);

    [ObservableProperty]
    public partial string? ArrangementNote { get; set; }

    internal void MakePrimary(DisplayEditItem item)
    {
        int index = Displays.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        IReadOnlyList<DisplayAssignment> updated = ProfileEditing.SetPrimary(Displays.Select(d => d.Assignment).ToList(), index);
        for (int i = 0; i < Displays.Count; i++)
        {
            Displays[i].Sync(updated[i]);
        }
    }

    /// <summary>A key combination pressed in the hotkey field; without Ctrl, Alt or Win it only shows a hint.</summary>
    internal void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        var hotkey = new Hotkey { Modifiers = modifiers, VirtualKey = virtualKey };
        if (!hotkey.IsValid)
        {
            HotkeyHint = Loc.Instance["Editor_HotkeyNeedsModifier"];
            return;
        }

        Hotkey = hotkey;
        HotkeyHint = Loc.Instance["Editor_HotkeyHint"];
        _log.Information("Editor recorded hotkey {Hotkey}", HotkeyText);
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        Hotkey = null;
        HotkeyHint = Loc.Instance["Editor_HotkeyHint"];
    }

    [RelayCommand]
    private void Remove(DisplayEditItem? item)
    {
        if (item is not null)
        {
            Displays.Remove(item);
            ArrangementNote = null;
        }
    }

    [RelayCommand]
    private async Task TakeCurrentAsync()
    {
        try
        {
            DisplaySnapshot snapshot = await Task.Run(() => _display.QueryAsync(CancellationToken.None));
            IReadOnlyList<DisplayAssignment> arrangement = ProfileEditing.CurrentArrangement(snapshot, Displays.Select(d => d.Assignment));
            SetDisplays(arrangement);
            ArrangementNote = Loc.Format("Editor_Taken", arrangement.Count);
            _log.Information("Editor took the current arrangement with {Count} displays", arrangement.Count);
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Current arrangement could not be read");
            Problems = ex.Message;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        Profile profile = Build();
        IReadOnlyList<ProfileProblem> problems = ProfileEditing.Validate(profile, _catalog.Profiles);
        if (problems.Count > 0)
        {
            Problems = string.Join(Environment.NewLine, problems.Select(p => Loc.Instance["Problem_" + p]));
            return;
        }

        // Hotkeys are suspended while the editor is open, so this sees only other applications.
        if (profile.Hotkey is { } hotkey && !_hotkeys.IsAvailable(hotkey))
        {
            Problems = Loc.Instance["Problem_HotkeyInUse"];
            return;
        }

        try
        {
            await _catalog.SaveAsync(profile, CancellationToken.None);
            Saved = profile;
            CloseRequested?.Invoke(this, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Profile {Profile} could not be saved", profile.Name);
            Problems = Loc.Format("Status_Error", ex.Message);
        }
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(this, false);

    private void SetDisplays(IEnumerable<DisplayAssignment> displays)
    {
        Displays.Clear();
        foreach (DisplayAssignment display in displays)
        {
            Displays.Add(new DisplayEditItem(this, display));
        }
    }

    private Profile Build() => _original with
    {
        Name = Name.Trim(),
        Icon = SelectedIcon?.Key,
        ConfirmTimeoutSeconds = UseOwnTimeout ? (int)Math.Clamp(Math.Round(OwnTimeoutSeconds ?? 0), 0, 120) : null,
        Hotkey = Hotkey,
        Displays = Displays.Select(d => d.Assignment).ToList(),
        Audio = _original.Audio with
        {
            Playback = AudioSlots[0].Endpoint,
            PlaybackCommunications = AudioSlots[1].Endpoint,
            Recording = AudioSlots[2].Endpoint,
            RecordingCommunications = AudioSlots[3].Endpoint,
        },
    };
}

/// <summary>One display row in the editor.</summary>
public sealed partial class DisplayEditItem : ObservableObject
{
    private readonly ProfileEditorViewModel _owner;
    private bool _syncing;

    public DisplayEditItem(ProfileEditorViewModel owner, DisplayAssignment assignment)
    {
        _owner = owner;
        Assignment = assignment;
        Sync(assignment);
    }

    public DisplayAssignment Assignment { get; private set; }

    public string Name => SwitchMessages.NameOf(Assignment.Identity);

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeOptional))]
    public partial bool IsPrimary { get; set; }

    [ObservableProperty]
    public partial bool IsOptional { get; set; }

    public bool CanBeOptional => !IsPrimary;

    internal void Sync(DisplayAssignment assignment)
    {
        _syncing = true;
        try
        {
            Assignment = assignment;
            IsPrimary = assignment.IsPrimary;
            IsOptional = assignment.IsOptional;
            double hertz = assignment.RefreshDenominator == 0 ? 0 : (double)assignment.RefreshNumerator / assignment.RefreshDenominator;
            ModeText = Loc.Format("Editor_Mode", assignment.Width, assignment.Height, hertz.ToString("0.##", Loc.Instance.Culture),
                assignment.PositionX, assignment.PositionY);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnIsPrimaryChanged(bool value)
    {
        if (!_syncing && value)
        {
            _owner.MakePrimary(this);
        }
    }

    partial void OnIsOptionalChanged(bool value)
    {
        if (!_syncing)
        {
            Assignment = Assignment with { IsOptional = value };
        }
    }
}

public sealed record AudioChoice(AudioEndpoint? Endpoint, string Name);

/// <summary>One audio role in the editor: "don't change" or a device of this machine.</summary>
public sealed partial class AudioSlot : ObservableObject
{
    public AudioSlot(string label, string noneText, IReadOnlyList<AudioDeviceInfo> devices, AudioEndpoint? current)
    {
        ArgumentNullException.ThrowIfNull(devices);

        Label = label;
        Choices.Add(new AudioChoice(null, noneText));
        foreach (AudioDeviceInfo device in devices.OrderByDescending(d => d.IsActive).ThenBy(d => d.Endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            string name = device.IsActive ? device.Endpoint.FriendlyName : Loc.Format("Audio_NotConnected", device.Endpoint.FriendlyName);
            Choices.Add(new AudioChoice(device.Endpoint, name));
        }

        if (current is not null && !devices.Any(d => SameDevice(d.Endpoint, current)))
        {
            // Keep a device this machine does not know (profile copied from another PC) instead of silently dropping it.
            Choices.Add(new AudioChoice(current, Loc.Format("Audio_Unknown", current.FriendlyName)));
        }

        Selected = current is null ? Choices[0] : Choices.First(c => c.Endpoint is { } e && SameDevice(e, current));
    }

    public string Label { get; }

    public ObservableCollection<AudioChoice> Choices { get; } = [];

    [ObservableProperty]
    public partial AudioChoice? Selected { get; set; }

    public AudioEndpoint? Endpoint => Selected?.Endpoint;

    private static bool SameDevice(AudioEndpoint a, AudioEndpoint b) =>
        string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);
}
