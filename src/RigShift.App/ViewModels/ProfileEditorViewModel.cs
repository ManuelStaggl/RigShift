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
/// Editor for one profile: name, icon, whether it asks after switching, which displays take part (primary, optional) and
/// audio. Resolutions and positions are not editable; they come from "use current arrangement". Refresh rate and HDR are chosen per display (section 6, item 10); display names only on the Displays page.
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject, IDisposable
{
    private static readonly IReadOnlyList<DisplayAssignment> NoDisplays = [];
    private static readonly IReadOnlyList<AppAction> NoApps = [];

    private readonly bool _isNew;
    private readonly IReadOnlyList<UsbDevice> _usbDevices;
    private readonly IReadOnlyDictionary<string, string>? _customUsbNames;
    private readonly IReadOnlyList<RuleDevice> _knownUsbDevices;
    private readonly string? _savedWaitDeviceId;
    private readonly string? _savedWaitDeviceName;
    private string _hotkeyHintKey = "Editor_HotkeyHint";

    private readonly Profile _original;
    private readonly Profile _initial;
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
        IReadOnlyDictionary<string, string>? usbDeviceNames,
        IReadOnlyList<RuleDevice> knownUsbDevices,
        bool confirmationEnabled,
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
        _isNew = isNew;
        _usbDevices = usbDevices;
        _customUsbNames = usbDeviceNames;
        _knownUsbDevices = knownUsbDevices;
        ConfirmationEnabled = confirmationEnabled;
        _savedWaitDeviceId = UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId);
        _savedWaitDeviceName = profile.AppsWaitForUsbDeviceName;
        Hotkey = profile.Hotkey;
        HotkeyHint = Loc.Instance[_hotkeyHintKey];
        _log = log.ForContext<ProfileEditorViewModel>();

        Title = Loc.Instance[isNew ? "Editor_TitleNew" : "Editor_TitleEdit"];
        Name = profile.Name;
        FillIconChoices(ProfileIcons.Normalize(profile.Icon));
        SwitchWithoutAsking = profile.SwitchWithoutAsking;
        SetDisplays(profile.Displays);

        AudioAssignment audio = profile.Audio;
        AudioSlots =
        [
            new AudioSlot("Audio_Playback", "Audio_Unchanged", playbackDevices, audio.Playback, audio.PlaybackVolumePercent, supportsVolume: true),
            new AudioSlot("Audio_Recording", "Audio_Unchanged", recordingDevices, audio.Recording, audio.RecordingVolumePercent, supportsVolume: true),
        ];
        CommunicationsAudioSlots =
        [
            new AudioSlot("Audio_PlaybackComms", "Audio_SameAsPlayback", playbackDevices, audio.PlaybackCommunications),
            new AudioSlot("Audio_RecordingComms", "Audio_SameAsRecording", recordingDevices, audio.RecordingCommunications),
        ];
        ShowCommunicationsAudio = audio.PlaybackCommunications is not null || audio.RecordingCommunications is not null;

        foreach (AppAction app in profile.Apps)
        {
            Apps.Add(new AppEditItem(app));
        }

        FillAppsWaitChoices(_savedWaitDeviceId);

        KeepAwake = profile.KeepAwake;
        DisableCommunicationsDucking = profile.DisableCommunicationsDucking;

        // The editor's own reading of the profile, so defaults it fills in do not count as changes.
        _initial = Build();

        // Texts built here follow a language change while the editor is open (I-13); Dispose unsubscribes.
        Loc.Instance.PropertyChanged += OnLanguageChanged;
    }

    /// <summary>True: saved, close the window. False: cancelled.</summary>
    public event EventHandler<bool>? CloseRequested;

    [ObservableProperty]
    public partial string Title { get; private set; }

    public ObservableCollection<Choice> IconChoices { get; } = [];

    public ObservableCollection<DisplayEditItem> Displays { get; } = [];

    /// <summary>Playback and recording.</summary>
    public IReadOnlyList<AudioSlot> AudioSlots { get; }

    /// <summary>Call devices, under "Advanced"; by default they follow playback and recording (analysis decision O-02).</summary>
    public IReadOnlyList<AudioSlot> CommunicationsAudioSlots { get; }

    /// <summary>"Advanced" starts open only when the profile already sets a call device, so nothing set stays hidden.</summary>
    public bool ShowCommunicationsAudio { get; }

    /// <summary>Anything differs from the profile as opened (analysis finding I-11).</summary>
    public bool HasChanges => !SameProfile(Build(), _initial);

    public ObservableCollection<AppEditItem> Apps { get; } = [];

    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    [ObservableProperty]
    public partial bool DisableCommunicationsDucking { get; set; }

    /// <summary>"Don't wait", the connected USB devices, and the saved device when it is not connected.</summary>
    public ObservableCollection<Choice> AppsWaitDeviceChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedAppsWaitDevice { get; set; }

    private readonly Dictionary<string, string> _usbDeviceNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The profile as saved, after <see cref="CloseRequested"/> with <c>true</c>.</summary>
    public Profile? Saved { get; private set; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedIcon { get; set; }

    [ObservableProperty]
    public partial bool SwitchWithoutAsking { get; set; }

    /// <summary>
    /// "Confirm after switching" is on in the settings. Off, every profile switches without asking, so the checkbox is
    /// disabled and a hint says where the setting is (finding HW-02).
    /// </summary>
    public bool ConfirmationEnabled { get; }

    public bool ConfirmationDisabled => !ConfirmationEnabled;

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
            SetHotkeyHint("Editor_HotkeyNeedsModifier");
            return;
        }

        Hotkey = hotkey;
        SetHotkeyHint("Editor_HotkeyHint");
        _log.Information("Editor recorded hotkey {Hotkey}", HotkeyText);
    }

    [RelayCommand]
    private void ClearHotkey()
    {
        Hotkey = null;
        SetHotkeyHint("Editor_HotkeyHint");
    }

    internal void AddApp(string path, string? name = null) => Apps.Add(new AppEditItem(new AppAction { Path = path, Name = name }));

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

    /// <summary>
    /// Offers the refresh rates each display reports at its resolution. A display that is off offers the rates it reported
    /// when it was last active; without any, a hint says why the list is short (finding HW-13).
    /// </summary>
    private async Task LoadRefreshRatesAsync()
    {
        var found = new List<(DisplayIdentity, int, int, IReadOnlyList<RefreshRate>)>();
        foreach (DisplayEditItem item in Displays.ToList())
        {
            DisplayAssignment assignment = item.Assignment;
            try
            {
                IReadOnlyList<RefreshRate> rates = await Task.Run(() =>
                    _display.ListRefreshRatesAsync(assignment.Identity, assignment.Width, assignment.Height, CancellationToken.None));
                if (rates.Count > 0)
                {
                    found.Add((assignment.Identity, assignment.Width, assignment.Height, rates));
                }
                else
                {
                    rates = _catalog.RememberedRefreshRates(assignment.Identity, assignment.Width, assignment.Height);
                    _log.Debug("{Display} is not active; offering {Count} remembered refresh rates", DisplayNames.Of(assignment), rates.Count);
                }

                item.OfferRefreshRates(rates);
            }
            catch (Exception ex) when (ex is Win32Exception or System.Runtime.InteropServices.COMException)
            {
                _log.Warning(ex, "Refresh rates of {Display} could not be read", DisplayNames.Of(assignment));
            }
        }

        await _catalog.RememberRefreshRatesAsync(found, CancellationToken.None);
    }

    public void Dispose() => Loc.Instance.PropertyChanged -= OnLanguageChanged;

    private void SetHotkeyHint(string key)
    {
        _hotkeyHintKey = key;
        HotkeyHint = Loc.Instance[key];
    }

    /// <summary>
    /// Rebuilds the texts made in code and keeps every selection by key. One-off messages (problems, "took N displays")
    /// are cleared rather than translated; they come back with the next action.
    /// </summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        Title = Loc.Instance[_isNew ? "Editor_TitleNew" : "Editor_TitleEdit"];
        HotkeyHint = Loc.Instance[_hotkeyHintKey];
        OnPropertyChanged(nameof(HotkeyText));
        Problems = null;
        ArrangementNote = null;

        FillIconChoices(SelectedIcon?.Key);
        FillAppsWaitChoices(SelectedAppsWaitDevice?.Key);
        foreach (AudioSlot slot in AudioSlots.Concat(CommunicationsAudioSlots))
        {
            slot.Relabel();
        }

        foreach (DisplayEditItem display in Displays)
        {
            display.Relabel();
        }

        foreach (AppEditItem app in Apps)
        {
            app.Relabel();
        }
    }

    private void FillIconChoices(string? selectedKey)
    {
        IconChoices.Clear();
        foreach (string key in ProfileIcons.All)
        {
            IconChoices.Add(new Choice(key, Loc.Instance["Icon_" + char.ToUpperInvariant(key[0]) + key[1..]]));
        }

        SelectedIcon = IconChoices.FirstOrDefault(c => c.Key == selectedKey) ?? IconChoices.First(c => c.Key == ProfileIcons.Rig);
    }

    /// <summary>
    /// Same source and naming as the automation page; the saved device and every other known device stay selectable while
    /// they are not connected (finding HW-08).
    /// </summary>
    private void FillAppsWaitChoices(string? selectedKey)
    {
        RuleDevice[] saved = _savedWaitDeviceId is null ? [] : [new RuleDevice { Id = _savedWaitDeviceId, Name = _savedWaitDeviceName }];
        UsbDeviceChoices.Fill(AppsWaitDeviceChoices, _usbDeviceNames, _usbDevices, [.. saved, .. _knownUsbDevices], _customUsbNames);
        AppsWaitDeviceChoices.Insert(0, new Choice(null, Loc.Instance["Editor_AppsWaitNone"]));

        SelectedAppsWaitDevice = AppsWaitDeviceChoices.FirstOrDefault(c => string.Equals(c.Key, selectedKey, StringComparison.OrdinalIgnoreCase))
            ?? AppsWaitDeviceChoices[0];
    }

    private Profile Build() => _original with
    {
        Name = Name.Trim(),
        Icon = SelectedIcon?.Key,
        SwitchWithoutAsking = SwitchWithoutAsking,
        ConfirmTimeoutSeconds = null,
        Hotkey = Hotkey,
        Displays = Displays.Select(d => d.Assignment).ToList(),
        Audio = _original.Audio with
        {
            Playback = AudioSlots[0].Endpoint,
            PlaybackCommunications = CommunicationsAudioSlots[0].Endpoint,
            Recording = AudioSlots[1].Endpoint,
            RecordingCommunications = CommunicationsAudioSlots[1].Endpoint,
            PlaybackVolumePercent = AudioSlots[0].VolumePercent,
            RecordingVolumePercent = AudioSlots[1].VolumePercent,
        },
        Apps = Apps.Select(a => a.ToAction()).ToList(),
        AppsWaitForUsbDeviceId = SelectedAppsWaitDevice?.Key,
        AppsWaitForUsbDeviceName = SelectedAppsWaitDevice?.Key is { } waitId && _usbDeviceNames.TryGetValue(waitId, out string? waitName) ? waitName : null,
        KeepAwake = KeepAwake,
        DisableCommunicationsDucking = DisableCommunicationsDucking,
    };

    /// <summary>Record equality compares lists by reference, so displays and apps are compared item by item.</summary>
    private static bool SameProfile(Profile a, Profile b) =>
        a with { Displays = NoDisplays, Apps = NoApps } == b with { Displays = NoDisplays, Apps = NoApps }
        && a.Displays.SequenceEqual(b.Displays)
        && a.Apps.SequenceEqual(b.Apps);
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

    /// <summary>"Name · Model"; the name is edited on the Displays page only (analysis decision O-05).</summary>
    public string Name => SwitchMessages.NameOf(Assignment);

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

    public ObservableCollection<HdrChoice> HdrChoices { get; } = [.. NewHdrChoices()];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SwitchesHdr))]
    public partial HdrChoice? SelectedHdr { get; set; }

    /// <summary>HDR is set on or off: the editor warns to try it in Windows first (finding HW-12).</summary>
    public bool SwitchesHdr => SelectedHdr?.Value is not null;

    /// <summary>The display offered no rates now and none are remembered, so only the saved one is listed (HW-13).</summary>
    [ObservableProperty]
    public partial bool RatesUnknown { get; private set; }

    /// <summary>New texts after a language change; <see cref="Sync"/> selects the same values again.</summary>
    internal void Relabel()
    {
        _syncing = true;
        try
        {
            HdrChoices.Clear();
            foreach (HdrChoice choice in NewHdrChoices())
            {
                HdrChoices.Add(choice);
            }
        }
        finally
        {
            _syncing = false;
        }

        Sync(Assignment);
    }

    private static HdrChoice[] NewHdrChoices() =>
    [
        new(null, Loc.Instance["Hdr_Unchanged"]),
        new(true, Loc.Instance["Hdr_On"]),
        new(false, Loc.Instance["Hdr_Off"]),
    ];

    internal void Sync(DisplayAssignment assignment)
    {
        _syncing = true;
        try
        {
            Assignment = assignment;
            IsPrimary = assignment.IsPrimary;
            IsOptional = assignment.IsOptional;
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
        RatesUnknown = rates.Count == 0;
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

        FillKindChoices();
        SelectedKind = KindChoices[action.Kind == AppActionKind.Stop ? 1 : 0];
        Path = action.Path;
        _pickedPath = action.Name is null ? null : action.Path;
        _pickedName = action.Name;
        Arguments = action.Arguments ?? string.Empty;
        WaitSeconds = action.WaitSeconds;
    }

    private string? _pickedPath;
    private string? _pickedName;

    /// <summary>Takes path and display name from the picker; the name only survives as long as the path stays the picked one.</summary>
    internal void SetPicked(string path, string? name)
    {
        Path = path;
        _pickedPath = name is null ? null : path;
        _pickedName = name;
    }

    public ObservableCollection<Choice> KindChoices { get; } = [];

    /// <summary>Null only for a moment while the list is rebuilt; that counts as "start".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart))]
    public partial Choice? SelectedKind { get; set; }

    /// <summary>Arguments only apply when starting.</summary>
    public bool IsStart => SelectedKind?.Key != nameof(AppActionKind.Stop);

    /// <summary>New texts after a language change, same selection.</summary>
    internal void Relabel()
    {
        bool start = IsStart;
        FillKindChoices();
        SelectedKind = KindChoices[start ? 0 : 1];
    }

    private void FillKindChoices()
    {
        KindChoices.Clear();
        KindChoices.Add(new Choice(nameof(AppActionKind.Start), Loc.Instance["App_Start"]));
        KindChoices.Add(new Choice(nameof(AppActionKind.Stop), Loc.Instance["App_Stop"]));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon))]
    public partial string Path { get; set; }

    /// <summary>The program's own icon; <c>null</c> while the path is not a file with one.</summary>
    public System.Windows.Media.ImageSource? Icon => AppIcons.Load(Path);

    [ObservableProperty]
    public partial string Arguments { get; set; }

    [ObservableProperty]
    public partial double? WaitSeconds { get; set; }

    public AppAction ToAction() => new()
    {
        Kind = IsStart ? AppActionKind.Start : AppActionKind.Stop,
        Path = Path.Trim(),
        Name = _pickedName is not null && string.Equals(Path.Trim(), _pickedPath, StringComparison.OrdinalIgnoreCase) ? _pickedName : null,
        Arguments = IsStart && !string.IsNullOrWhiteSpace(Arguments) ? Arguments.Trim() : null,
        WaitSeconds = (int)Math.Clamp(Math.Round(WaitSeconds ?? 0), 0, 300),
    };
}

public sealed record AudioChoice(AudioEndpoint? Endpoint, string Name);

/// <summary>One audio role in the editor: "don't change" or a device of this machine.</summary>
public sealed partial class AudioSlot : ObservableObject
{
    private readonly string _labelKey;
    private readonly string _noneKey;
    private readonly IReadOnlyList<AudioDeviceInfo> _devices;
    private readonly AudioEndpoint? _saved;

    /// <param name="labelKey">Text key of the role, e.g. <c>Audio_Playback</c>.</param>
    /// <param name="noneKey">Text key of the "don't change" entry.</param>
    public AudioSlot(
        string labelKey, string noneKey, IReadOnlyList<AudioDeviceInfo> devices, AudioEndpoint? current, int? volume = null, bool supportsVolume = false)
    {
        ArgumentNullException.ThrowIfNull(devices);

        _labelKey = labelKey;
        _noneKey = noneKey;
        _devices = devices;
        _saved = current;
        Label = Loc.Instance[labelKey];
        SupportsVolume = supportsVolume;
        SetVolume = volume is not null;
        Volume = volume ?? 50;
        FillChoices(current);
    }

    [ObservableProperty]
    public partial string Label { get; private set; }

    /// <summary>New texts after a language change, same device.</summary>
    internal void Relabel()
    {
        Label = Loc.Instance[_labelKey];
        FillChoices(Endpoint);
        OnPropertyChanged(nameof(VolumeText));
    }

    private void FillChoices(AudioEndpoint? selected)
    {
        Choices.Clear();
        Choices.Add(new AudioChoice(null, Loc.Instance[_noneKey]));
        foreach (AudioDeviceInfo device in _devices.OrderByDescending(d => d.IsActive).ThenBy(d => d.Endpoint.FriendlyName, StringComparer.CurrentCultureIgnoreCase))
        {
            string name = device.IsActive ? device.Endpoint.FriendlyName : Loc.Format("Audio_NotConnected", device.Endpoint.FriendlyName);
            Choices.Add(new AudioChoice(device.Endpoint, name));
        }

        if (_saved is not null && !_devices.Any(d => SameDevice(d.Endpoint, _saved)))
        {
            // Keep a device this machine does not know (profile copied from another PC) instead of silently dropping it.
            Choices.Add(new AudioChoice(_saved, Loc.Format("Audio_Unknown", _saved.FriendlyName)));
        }

        Selected = selected is null ? Choices[0] : Choices.First(c => c.Endpoint is { } e && SameDevice(e, selected));
    }

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
