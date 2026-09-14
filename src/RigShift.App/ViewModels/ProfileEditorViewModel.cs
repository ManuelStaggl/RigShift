using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// Editor for one profile: name, icon, confirmation time, which displays take part (primary, optional) and audio.
/// Resolutions and positions are not editable; they come from "use current arrangement" (docs/PLAN.md, section 10, M4).
/// Refresh rate and HDR are chosen per display (section 6, item 10).
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
        IReadOnlyList<UsbDevice> usbDevices,
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
            new AudioSlot(Loc.Instance["Audio_Playback"], Loc.Instance["Audio_Unchanged"], playbackDevices, audio.Playback, audio.PlaybackVolumePercent, supportsVolume: true),
            new AudioSlot(Loc.Instance["Audio_PlaybackComms"], Loc.Instance["Audio_SameAsAbove"], playbackDevices, audio.PlaybackCommunications),
            new AudioSlot(Loc.Instance["Audio_Recording"], Loc.Instance["Audio_Unchanged"], recordingDevices, audio.Recording, audio.RecordingVolumePercent, supportsVolume: true),
            new AudioSlot(Loc.Instance["Audio_RecordingComms"], Loc.Instance["Audio_SameAsAbove"], recordingDevices, audio.RecordingCommunications),
        ];

        foreach (AppAction app in profile.Apps)
        {
            Apps.Add(new AppEditItem(app));
        }

        // Same source and naming as the automation page; a saved device that is not connected stays selectable.
        AppsWaitDeviceChoices.Add(new Choice(null, Loc.Instance["Editor_AppsWaitNone"]));
        foreach (UsbDevice device in usbDevices)
        {
            AppsWaitDeviceChoices.Add(new Choice(device.Id, device.Name));
            _usbDeviceNames[device.Id] = device.Name;
        }

        if (UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId) is { } waitId && !_usbDeviceNames.ContainsKey(waitId))
        {
            string name = profile.AppsWaitForUsbDeviceName ?? waitId;
            AppsWaitDeviceChoices.Add(new Choice(waitId, Loc.Format("Automation_DeviceNotConnected", name)));
            _usbDeviceNames[waitId] = name;
        }

        SelectedAppsWaitDevice = AppsWaitDeviceChoices.FirstOrDefault(c => string.Equals(c.Key, UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId), StringComparison.OrdinalIgnoreCase))
            ?? AppsWaitDeviceChoices[0];
        AppsWaitSeconds = Profile.ClampAppsWaitSeconds(profile.AppsWaitSeconds);

        KeepAwake = profile.KeepAwake;
        DisableCommunicationsDucking = profile.DisableCommunicationsDucking;
    }

    /// <summary>True: saved, close the window. False: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    public string Title { get; }

    public string TimeoutHint { get; }

    public ObservableCollection<Choice> IconChoices { get; }

    public ObservableCollection<DisplayEditItem> Displays { get; } = [];

    public IReadOnlyList<AudioSlot> AudioSlots { get; }

    public ObservableCollection<AppEditItem> Apps { get; } = [];

    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    [ObservableProperty]
    public partial bool DisableCommunicationsDucking { get; set; }

    /// <summary>"Don't wait", the connected USB devices, and the saved device when it is not connected.</summary>
    public ObservableCollection<Choice> AppsWaitDeviceChoices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsWaitDevice))]
    public partial Choice? SelectedAppsWaitDevice { get; set; }

    /// <summary>The wait time only matters once a device is chosen.</summary>
    public bool HasAppsWaitDevice => SelectedAppsWaitDevice?.Key is not null;

    [ObservableProperty]
    public partial double? AppsWaitSeconds { get; set; }

    private readonly Dictionary<string, string> _usbDeviceNames = new(StringComparer.OrdinalIgnoreCase);

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
    private void AddApp() => Apps.Add(new AppEditItem(new AppAction { Path = string.Empty }));

    [RelayCommand]
    private void RemoveApp(AppEditItem? item)
    {
        if (item is not null)
        {
            Apps.Remove(item);
        }
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
            IReadOnlyList<DisplayAssignment> arrangement = ProfileEditing.CurrentArrangement(snapshot, Displays.Select(d => d.Assignment), _catalog.KnownDisplayNames);
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

        _ = LoadRefreshRatesAsync();
    }

    /// <summary>Offers the refresh rates each display reports at its resolution; displays that are off keep only their own.</summary>
    private async Task LoadRefreshRatesAsync()
    {
        foreach (DisplayEditItem item in Displays.ToList())
        {
            DisplayAssignment assignment = item.Assignment;
            try
            {
                IReadOnlyList<RefreshRate> rates = await Task.Run(() =>
                    _display.ListRefreshRatesAsync(assignment.Identity, assignment.Width, assignment.Height, CancellationToken.None));
                item.OfferRefreshRates(rates);
            }
            catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
            {
                _log.Warning(ex, "Refresh rates of {Display} could not be read", DisplayNames.Of(assignment));
            }
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
            PlaybackVolumePercent = AudioSlots[0].VolumePercent,
            RecordingVolumePercent = AudioSlots[2].VolumePercent,
        },
        Apps = Apps.Select(a => a.ToAction()).ToList(),
        AppsWaitForUsbDeviceId = SelectedAppsWaitDevice?.Key,
        AppsWaitForUsbDeviceName = SelectedAppsWaitDevice?.Key is { } waitId && _usbDeviceNames.TryGetValue(waitId, out string? waitName) ? waitName : null,
        AppsWaitSeconds = AppsWaitSeconds is { } seconds ? Profile.ClampAppsWaitSeconds((int)Math.Round(seconds)) : Profile.DefaultAppsWaitSeconds,
        KeepAwake = KeepAwake,
        DisableCommunicationsDucking = DisableCommunicationsDucking,
    };
}

public sealed record RefreshChoice(RefreshRate Rate)
{
    public string Text => Rate.Hertz.ToString("0.##", Loc.Instance.Culture) + " Hz";
}

public sealed record HdrChoice(bool? Value, string Text);

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

    public string Name => SwitchMessages.NameOf(Assignment);

    /// <summary>The monitor model, shown as placeholder of the name field.</summary>
    public string ModelName => SwitchMessages.NameOf(null, Assignment.Identity);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name))]
    public partial string CustomName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanBeOptional))]
    public partial bool IsPrimary { get; set; }

    [ObservableProperty]
    public partial bool IsOptional { get; set; }

    public bool CanBeOptional => !IsPrimary;

    public ObservableCollection<RefreshChoice> RefreshChoices { get; } = [];

    [ObservableProperty]
    public partial RefreshChoice? SelectedRefresh { get; set; }

    public IReadOnlyList<HdrChoice> HdrChoices { get; } =
    [
        new(null, Loc.Instance["Hdr_Unchanged"]),
        new(true, Loc.Instance["Hdr_On"]),
        new(false, Loc.Instance["Hdr_Off"]),
    ];

    [ObservableProperty]
    public partial HdrChoice? SelectedHdr { get; set; }

    internal void Sync(DisplayAssignment assignment)
    {
        _syncing = true;
        try
        {
            Assignment = assignment;
            IsPrimary = assignment.IsPrimary;
            IsOptional = assignment.IsOptional;
            CustomName = assignment.CustomName ?? string.Empty;
            ModeText = Loc.Format("Editor_Mode", assignment.Width, assignment.Height, assignment.PositionX, assignment.PositionY);

            RefreshRate rate = RefreshRate.Of(assignment);
            if (!RefreshChoices.Any(c => c.Rate == rate))
            {
                RefreshChoices.Add(new RefreshChoice(rate));
            }

            SelectedRefresh = RefreshChoices.First(c => c.Rate == rate);
            SelectedHdr = HdrChoices.First(c => c.Value == assignment.Hdr);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>Adds the rates the display offers. The saved rate stays, also when the list has one that looks the same.</summary>
    internal void OfferRefreshRates(IReadOnlyList<RefreshRate> rates)
    {
        RefreshRate current = RefreshRate.Of(Assignment);
        List<RefreshRate> all = [current, .. rates.Where(r => !r.LooksLike(current))];
        _syncing = true;
        try
        {
            RefreshChoices.Clear();
            foreach (RefreshRate rate in all.OrderByDescending(r => r.Hertz))
            {
                RefreshChoices.Add(new RefreshChoice(rate));
            }

            SelectedRefresh = RefreshChoices.First(c => c.Rate == current);
        }
        finally
        {
            _syncing = false;
        }
    }

    partial void OnSelectedRefreshChanged(RefreshChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { RefreshNumerator = value.Rate.Numerator, RefreshDenominator = value.Rate.Denominator };
        }
    }

    partial void OnSelectedHdrChanged(HdrChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { Hdr = value.Value };
        }
    }

    partial void OnIsPrimaryChanged(bool value)
    {
        if (!_syncing && value)
        {
            _owner.MakePrimary(this);
        }
    }

    partial void OnCustomNameChanged(string value)
    {
        if (!_syncing)
        {
            Assignment = Assignment with { CustomName = DisplayNames.Normalize(value) };
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

/// <summary>One app entry in the editor.</summary>
public sealed partial class AppEditItem : ObservableObject
{
    public AppEditItem(AppAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        KindChoices = [new Choice(nameof(AppActionKind.Start), Loc.Instance["App_Start"]), new Choice(nameof(AppActionKind.Stop), Loc.Instance["App_Stop"])];
        SelectedKind = KindChoices[action.Kind == AppActionKind.Stop ? 1 : 0];
        Path = action.Path;
        Arguments = action.Arguments ?? string.Empty;
        WaitSeconds = action.WaitSeconds;
    }

    public IReadOnlyList<Choice> KindChoices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart))]
    public partial Choice SelectedKind { get; set; }

    /// <summary>Arguments only apply when starting.</summary>
    public bool IsStart => SelectedKind.Key == nameof(AppActionKind.Start);

    [ObservableProperty]
    public partial string Path { get; set; }

    [ObservableProperty]
    public partial string Arguments { get; set; }

    [ObservableProperty]
    public partial double? WaitSeconds { get; set; }

    public AppAction ToAction() => new()
    {
        Kind = IsStart ? AppActionKind.Start : AppActionKind.Stop,
        Path = Path.Trim(),
        Arguments = IsStart && !string.IsNullOrWhiteSpace(Arguments) ? Arguments.Trim() : null,
        WaitSeconds = (int)Math.Clamp(Math.Round(WaitSeconds ?? 0), 0, 300),
    };
}

public sealed record AudioChoice(AudioEndpoint? Endpoint, string Name);

/// <summary>One audio role in the editor: "don't change" or a device of this machine.</summary>
public sealed partial class AudioSlot : ObservableObject
{
    public AudioSlot(
        string label, string noneText, IReadOnlyList<AudioDeviceInfo> devices, AudioEndpoint? current, int? volume = null, bool supportsVolume = false)
    {
        ArgumentNullException.ThrowIfNull(devices);

        Label = label;
        SupportsVolume = supportsVolume;
        SetVolume = volume is not null;
        Volume = volume ?? 50;
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
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    public partial AudioChoice? Selected { get; set; }

    public AudioEndpoint? Endpoint => Selected?.Endpoint;

    /// <summary>Only playback and recording get a volume; the call roles usually share their device.</summary>
    public bool SupportsVolume { get; }

    /// <summary>A volume belongs to a device, so it can only be set once one is chosen.</summary>
    public bool HasDevice => Endpoint is not null;

    [ObservableProperty]
    public partial bool SetVolume { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeText))]
    public partial double Volume { get; set; }

    public string VolumeText => Loc.Format("Audio_VolumeValue", (int)Math.Round(Volume));

    public int? VolumePercent => SupportsVolume && SetVolume && HasDevice ? (int)Math.Clamp(Math.Round(Volume), 0, 100) : null;

    private static bool SameDevice(AudioEndpoint a, AudioEndpoint b) =>
        string.Equals(a.EndpointId, b.EndpointId, StringComparison.OrdinalIgnoreCase);
}
