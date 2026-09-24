using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
/// The detail of one profile on the profiles page: seeing and editing are the same view (R-NAV-3). Every change is
/// validated at once; the save bar shows the problem count, and saving writes the profile and its USB rules together.
/// Resolutions and positions are not editable; they come from "use current arrangement" (docs/display-topology.md).
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject, IDetailEditor
{
    private static readonly IReadOnlyList<DisplayAssignment> NoDisplays = [];
    private static readonly IReadOnlyList<AppAction> NoApps = [];

    private readonly IReadOnlyList<UsbDevice> _usbDevices;
    private readonly IReadOnlyDictionary<string, string>? _customUsbNames;
    private readonly IReadOnlyList<RuleDevice> _knownUsbDevices;
    private readonly string? _savedWaitDeviceId;
    private readonly string? _savedWaitDeviceName;
    private string _hotkeyHintKey = "Editor_HotkeyHint";
    private HotkeyUse? _hotkeyConflict;
    private SurroundGrid? _surroundGrid;
    private IReadOnlySet<string> _missingDisplays = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _loading = true;

    private const string SurroundUnchanged = "unchanged";
    private const string SurroundOff = "off";
    private const string SurroundOn = "on";

    private readonly Profile _original;
    private Profile _initial;
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IDesktopIcons _desktopIcons;
    private readonly HotkeyService _hotkeys;
    private readonly SettingsService _settings;
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
        SurroundState surround,
        ProfileRulesEditor rules,
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IDesktopIcons desktopIcons,
        HotkeyService hotkeys,
        SettingsService settings,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(log);

        _original = profile;
        Saved = profile;
        _catalog = catalog;
        _display = display;
        _hotkeys = hotkeys;
        _settings = settings;
        IsNew = isNew;
        _usbDevices = usbDevices;
        _customUsbNames = usbDeviceNames;
        _knownUsbDevices = knownUsbDevices;
        ConfirmationEnabled = confirmationEnabled;
        _savedWaitDeviceId = UsbDeviceIds.Normalize(profile.AppsWaitForUsbDeviceId);
        _savedWaitDeviceName = profile.AppsWaitForUsbDeviceName;
        Hotkey = profile.Hotkey;
        HotkeyHint = HotkeyHintText();
        Rules = rules;
        _log = log.ForContext<ProfileEditorViewModel>();

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
        foreach (AudioSlot slot in AudioSlots.Concat(CommunicationsAudioSlots))
        {
            slot.PropertyChanged += OnPartChanged;
        }

        foreach (AppAction app in profile.Apps)
        {
            Apps.Add(new AppEditItem(app));
        }

        FillAppsWaitChoices(_savedWaitDeviceId);

        _desktopIcons = desktopIcons;
        DesktopIcons = profile.DesktopIcons;
        KeepAwake = profile.KeepAwake;
        DisableCommunicationsDucking = profile.DisableCommunicationsDucking;
        FillSurroundChoices(surround, profile.Surround);

        // The editor's own reading of the profile, so defaults it fills in do not count as changes.
        _initial = Build();
        _loading = false;

        Displays.CollectionChanged += OnDisplaysChanged;
        Apps.CollectionChanged += OnAppsChanged;
        foreach (AppEditItem app in Apps)
        {
            app.PropertyChanged += OnPartChanged;
        }

        rules.Changed += OnPartChanged;

        // Texts built here follow a language change while the editor is open (I-13); Dispose unsubscribes.
        Loc.Instance.PropertyChanged += OnLanguageChanged;
        Recalculate();
    }

    public Guid Id => _original.Id;

    /// <summary>The profile as it is on disk: as loaded, then as last saved. The page compares it with the catalog.</summary>
    public Profile Saved { get; private set; }

    /// <summary>Not saved yet: the save bar stays until the first save, and switching is not possible (F3).</summary>
    [ObservableProperty]
    public partial bool IsNew { get; private set; }

    /// <summary>The USB rules of this profile, saved with it (R-OBJ-1).</summary>
    public ProfileRulesEditor Rules { get; }

    public ObservableCollection<Choice> IconChoices { get; } = [];

    public ObservableCollection<DisplayEditItem> Displays { get; } = [];

    /// <summary>Playback and recording.</summary>
    public IReadOnlyList<AudioSlot> AudioSlots { get; }

    /// <summary>Call devices, under "Devices for calls"; by default they follow playback and recording (analysis decision O-02).</summary>
    public IReadOnlyList<AudioSlot> CommunicationsAudioSlots { get; }

    /// <summary>The call devices start open only when the profile already sets one, so nothing set stays hidden.</summary>
    public bool ShowCommunicationsAudio { get; }

    /// <summary>Anything differs from the profile on disk, or the profile is not on disk yet.</summary>
    [ObservableProperty]
    public partial bool IsDirty { get; private set; }

    /// <summary>Validation problems as they stand; the save bar disables Save while there are any.</summary>
    [ObservableProperty]
    public partial int ProblemCount { get; private set; }

    /// <summary>The name's problem, shown under the name field in the detail head.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNameProblem))]
    public partial string? NameProblem { get; private set; }

    public bool HasNameProblem => NameProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDisplaysProblem))]
    public partial string? DisplaysProblem { get; private set; }

    public bool HasDisplaysProblem => DisplaysProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHotkeyProblem))]
    public partial string? HotkeyProblem { get; private set; }

    public bool HasHotkeyProblem => HotkeyProblem is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAppsProblem))]
    public partial string? AppsProblem { get; private set; }

    public bool HasAppsProblem => AppsProblem is not null;

    /// <summary>What the planner would warn about (head budget, EDID fallback, twins); one bar above the topology.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial string? WarningText { get; private set; }

    public bool HasWarning => WarningText is not null;

    /// <summary>A save that failed on disk; cleared by the next successful save.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>The picture of this profile's displays: size S in the list and head, size L on the displays tab.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TopologyDisplay> TopologyDisplays { get; private set; } = [];

    /// <summary>Key (device path) of the display chosen in the picture; its properties are edited in the card below.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDisplay), nameof(HasSelectedDisplay))]
    public partial string? SelectedDisplayKey { get; set; }

    public DisplayEditItem? SelectedDisplay =>
        Displays.FirstOrDefault(d => string.Equals(d.Key, SelectedDisplayKey, StringComparison.OrdinalIgnoreCase));

    public bool HasSelectedDisplay => SelectedDisplay is not null;

    public ObservableCollection<AppEditItem> Apps { get; } = [];

    [ObservableProperty]
    public partial bool KeepAwake { get; set; }

    /// <summary>
    /// Where the desktop symbols belong in this profile, or <c>null</c> to leave them alone. Captured on demand and not
    /// on every save: the positions are only right while this profile's arrangement is the one on screen, and saving an
    /// unrelated change from the other arrangement would quietly overwrite good ones.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DesktopIconsText))]
    [NotifyPropertyChangedFor(nameof(HasDesktopIcons))]
    public partial DesktopIconLayout? DesktopIcons { get; set; }

    public bool HasDesktopIcons => DesktopIcons is { IsEmpty: false };

    public string DesktopIconsText => DesktopIcons is { IsEmpty: false } layout
        ? Loc.Format(
            layout.Icons.Count == 1 ? "Editor_DesktopIconsSavedOne" : "Editor_DesktopIconsSaved",
            layout.Icons.Count,
            layout.CapturedAt.ToLocalTime().ToString("g", Loc.Instance.Culture))
        : Loc.Instance["Editor_DesktopIconsNone"];

    [RelayCommand]
    private void CaptureDesktopIcons()
    {
        if (_desktopIcons.Capture() is { IsEmpty: false } layout)
        {
            DesktopIcons = layout;
            _log.Information("Desktop symbols captured for {Profile}: {Count}", Name, layout.Icons.Count);
        }
        else
        {
            _log.Warning("Desktop symbols not captured for {Profile}: the desktop reported none", Name);
        }
    }

    [RelayCommand]
    private void ClearDesktopIcons() => DesktopIcons = null;

    [ObservableProperty]
    public partial bool DisableCommunicationsDucking { get; set; }

    /// <summary>"Leave alone", "off", and "on" once a grid is known. Empty on a machine Surround is no topic for.</summary>
    public ObservableCollection<Choice> SurroundChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedSurround { get; set; }

    /// <summary>The grid in words, or why there is none to switch on.</summary>
    [ObservableProperty]
    public partial string SurroundHint { get; private set; } = string.Empty;

    /// <summary>The hint is a problem when the profile wants Surround on but there is no grid to switch to.</summary>
    [ObservableProperty]
    public partial bool SurroundHintIsError { get; private set; }

    /// <summary>False hides the whole section: a machine without an NVIDIA card has nothing to say here.</summary>
    public bool ShowSurround { get; private set; }

    /// <summary>"Don't wait", the connected USB devices, and the saved device when it is not connected.</summary>
    public ObservableCollection<Choice> AppsWaitDeviceChoices { get; } = [];

    [ObservableProperty]
    public partial Choice? SelectedAppsWaitDevice { get; set; }

    private readonly Dictionary<string, string> _usbDeviceNames = new(StringComparer.OrdinalIgnoreCase);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText))]
    public partial string Name { get; set; }

    /// <summary>The URL that switches to this profile from a shortcut, a Stream Deck or the command line.</summary>
    public string CommandText => "rigshift://apply/" + Uri.EscapeDataString(Name.Trim());

    [ObservableProperty]
    public partial Choice? SelectedIcon { get; set; }

    [RelayCommand]
    private void ChooseIcon(Choice? icon)
    {
        if (icon is not null)
        {
            SelectedIcon = icon;
        }
    }

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

    /// <summary>The combination, or the placeholder while there is none (the field is read-only, so it has no placeholder of its own).</summary>
    public string HotkeyText => Hotkey is null ? Loc.Instance["Editor_HotkeyPlaceholder"] : HotkeyFormat.Format(Hotkey);

    public bool HasHotkey => Hotkey is not null;

    [ObservableProperty]
    public partial string HotkeyHint { get; set; }

    [ObservableProperty]
    public partial string? ArrangementNote { get; set; }

    /// <summary>Recording a hotkey RigShift holds would switch right away, so they rest while the field has the focus.</summary>
    public void BeginHotkeyRecording() => _hotkeys.Suspend();

    public void EndHotkeyRecording() => _hotkeys.Resume();

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

        OnDisplaysEdited();
    }

    /// <summary>A display's property changed (rate, HDR, optional, name): picture and validation follow.</summary>
    internal void OnDisplaysEdited()
    {
        if (_loading)
        {
            return;
        }

        UpdateTopology();
        Recalculate();
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

        // Hotkeys are suspended while the field has the focus, so this sees only other applications.
        if (!_hotkeys.IsAvailable(hotkey))
        {
            SetHotkeyHint("Problem_HotkeyInUse");
            return;
        }

        // The own hotkeys are released right now, so Windows cannot tell that a profile, a game or "back" holds this one.
        if (_hotkeys.UsedBy(hotkey, HotkeyUseKind.Profile, _original.Id) is { } use)
        {
            _hotkeyConflict = use;
            HotkeyHint = HotkeyHintText();
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
    private void MoveAppUp(AppEditItem? item) => MoveApp(item, -1);

    [RelayCommand]
    private void MoveAppDown(AppEditItem? item) => MoveApp(item, 1);

    private void MoveApp(AppEditItem? item, int offset)
    {
        int index = item is null ? -1 : Apps.IndexOf(item);
        int target = index + offset;
        if (index >= 0 && target >= 0 && target < Apps.Count)
        {
            Apps.Move(index, target);
        }
    }

    [RelayCommand]
    private void RemoveDisplay(DisplayEditItem? item)
    {
        if (item is null)
        {
            return;
        }

        Displays.Remove(item);
        ArrangementNote = null;
        if (SelectedDisplay is null)
        {
            SelectedDisplayKey = null;
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
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>The planner's view of this profile against the live displays: missing ones in the picture, warnings above it.</summary>
    public void ShowPlan(TopologyPlan? plan)
    {
        _missingDisplays = plan is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : plan.Missing.Select(m => m.Assignment.Identity.TargetDevicePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        WarningText = plan is null || plan.Warnings.Count == 0
            ? null
            : string.Join(" ", plan.Warnings.Select(w => w.Kind).Distinct().Select(kind => Loc.Instance["Warning_" + kind]));
        UpdateTopology();
    }

    /// <summary>Writes the profile and its USB rules. False: a problem remains or the disk said no; the detail shows why.</summary>
    public async Task<bool> SaveAsync()
    {
        Recalculate();
        if (ProblemCount > 0)
        {
            return false;
        }

        Profile profile = Build();
        try
        {
            await _catalog.SaveAsync(profile, CancellationToken.None);
            Saved = profile;
            if (Rules.IsDirty)
            {
                await _settings.UpdateAsync(s => s with { AutomationRules = Rules.MergeInto(s.AutomationRules) }, CancellationToken.None);
                Rules.MarkSaved();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Profile {Profile} could not be saved", profile.Name);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
            return false;
        }

        _initial = profile;
        ErrorMessage = null;
        IsNew = false;
        Recalculate();
        _log.Information("Profile {Profile} saved from the detail", profile.Name);
        return true;
    }

    private void SetDisplays(IEnumerable<DisplayAssignment> displays)
    {
        Displays.CollectionChanged -= OnDisplaysChanged;
        foreach (DisplayEditItem old in Displays)
        {
            old.PropertyChanged -= OnPartChanged;
        }

        Displays.Clear();
        foreach (DisplayAssignment display in displays)
        {
            var item = new DisplayEditItem(this, display);
            item.PropertyChanged += OnPartChanged;
            Displays.Add(item);
        }

        Displays.CollectionChanged += OnDisplaysChanged;
        if (SelectedDisplay is null)
        {
            SelectedDisplayKey = Displays.FirstOrDefault(d => d.IsPrimary)?.Key ?? Displays.FirstOrDefault()?.Key;
        }

        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(HasSelectedDisplay));
        UpdateTopology();
        if (!_loading)
        {
            Recalculate();
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

    public void Dispose()
    {
        Loc.Instance.PropertyChanged -= OnLanguageChanged;
        Rules.Changed -= OnPartChanged;
    }

    /// <summary>The hint in the current language; a combination taken inside RigShift names who holds it.</summary>
    private string HotkeyHintText() => _hotkeyConflict is { } use ? HotkeyService.UsedByText(use) : Loc.Instance[_hotkeyHintKey];

    private void SetHotkeyHint(string key)
    {
        _hotkeyConflict = null;
        _hotkeyHintKey = key;
        HotkeyHint = Loc.Instance[key];
    }

    private void OnDisplaysChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (DisplayEditItem item in e.OldItems?.OfType<DisplayEditItem>() ?? [])
        {
            item.PropertyChanged -= OnPartChanged;
        }

        foreach (DisplayEditItem item in e.NewItems?.OfType<DisplayEditItem>() ?? [])
        {
            item.PropertyChanged += OnPartChanged;
        }

        OnPropertyChanged(nameof(SelectedDisplay));
        OnPropertyChanged(nameof(HasSelectedDisplay));
        OnDisplaysEdited();
    }

    private void OnAppsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (AppEditItem item in e.OldItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged -= OnPartChanged;
        }

        foreach (AppEditItem item in e.NewItems?.OfType<AppEditItem>() ?? [])
        {
            item.PropertyChanged += OnPartChanged;
        }

        Recalculate();
    }

    private void OnPartChanged(object? sender, EventArgs e) => Recalculate();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(Name) or nameof(SelectedIcon) or nameof(SwitchWithoutAsking) or nameof(Hotkey)
            or nameof(KeepAwake) or nameof(DisableCommunicationsDucking) or nameof(DesktopIcons) or nameof(SelectedSurround)
            or nameof(SelectedAppsWaitDevice))
        {
            Recalculate();
        }
    }

    /// <summary>Validation and the dirty flag after every change; cheap enough to run on each keystroke.</summary>
    private void Recalculate()
    {
        if (_loading)
        {
            return;
        }

        Profile built = Build();
        IReadOnlyList<ProfileProblem> problems = ProfileEditing.Validate(built, _catalog.Profiles);
        ProblemCount = problems.Count;
        NameProblem = TextOf(problems, ProfileProblem.NameMissing, ProfileProblem.NameTooLong, ProfileProblem.NameTaken);
        DisplaysProblem = TextOf(problems, ProfileProblem.NoDisplays, ProfileProblem.NoSinglePrimary, ProfileProblem.PrimaryIsOptional);
        HotkeyProblem = TextOf(problems, ProfileProblem.HotkeyInvalid, ProfileProblem.HotkeyTaken);
        AppsProblem = TextOf(problems, ProfileProblem.AppPathMissing);
        IsDirty = IsNew || Rules.IsDirty || !SameProfile(built, _initial);
        UpdateSurroundHint();
    }

    private static string? TextOf(IReadOnlyList<ProfileProblem> problems, params ProfileProblem[] kinds)
    {
        List<string> texts = kinds.Where(problems.Contains).Select(p => Loc.Instance["Problem_" + p]).ToList();
        return texts.Count == 0 ? null : string.Join(" ", texts);
    }

    private void UpdateTopology() => TopologyDisplays = Services.TopologyDisplays.From(Displays.Select(d => d.Assignment), _missingDisplays);

    /// <summary>
    /// Rebuilds the texts made in code and keeps every selection by key. One-off messages (errors, "took N displays")
    /// are cleared rather than translated; they come back with the next action.
    /// </summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        HotkeyHint = HotkeyHintText();
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(DesktopIconsText));
        ErrorMessage = null;
        ArrangementNote = null;

        _loading = true;
        try
        {
            FillIconChoices(SelectedIcon?.Key);
            FillAppsWaitChoices(SelectedAppsWaitDevice?.Key);
            string? surround = SelectedSurround?.Key;
            SurroundChoices.Clear();
            FillSurroundChoices(_surroundState, _original.Surround, surround);
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

            Rules.Relabel();
        }
        finally
        {
            _loading = false;
        }

        UpdateTopology();
        Recalculate();
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

    private SurroundState _surroundState = SurroundState.Unavailable(SurroundAvailability.Unknown, string.Empty);

    /// <summary>
    /// Surround has three answers per profile: leave it alone (the default, and what every profile before 1.9 means),
    /// switch it off, or run this grid. There is no grid editor: a grid is built once in the NVIDIA control panel and
    /// taken over from there, because the driver needs a reload to create one and that closes running games.
    /// </summary>
    private void FillSurroundChoices(SurroundState state, SurroundSetting? saved, string? selectedKey = null)
    {
        _surroundState = state;
        // Nothing to offer without an NVIDIA driver - unless the profile already carries a setting from another machine.
        ShowSurround = state.Availability == SurroundAvailability.Available || saved is not null;
        if (!ShowSurround)
        {
            return;
        }

        _surroundGrid = saved?.Grid ?? (state.Grids.Count > 0 ? state.Grids[0] : null);
        SurroundChoices.Add(new Choice(SurroundUnchanged, Loc.Instance["Editor_SurroundUnchanged"]));
        SurroundChoices.Add(new Choice(SurroundOff, Loc.Instance["Editor_SurroundOff"]));
        SurroundChoices.Add(new Choice(SurroundOn, Loc.Instance["Editor_SurroundOn"]));

        string wanted = selectedKey ?? (saved is null ? SurroundUnchanged : saved.Enabled ? SurroundOn : SurroundOff);
        SelectedSurround = SurroundChoices.FirstOrDefault(c => c.Key == wanted) ?? SurroundChoices[0];
        UpdateSurroundHint();
    }

    private void UpdateSurroundHint()
    {
        if (!ShowSurround)
        {
            return;
        }

        bool wantsOn = SelectedSurround?.Key == SurroundOn;
        if (_surroundGrid is { } grid)
        {
            SurroundHint = Loc.Format("Editor_SurroundGrid", grid.Displays.Count, grid.Width, grid.Height, grid.TotalWidth, grid.TotalHeight);
            SurroundHintIsError = false;
            return;
        }

        SurroundHint = _surroundState.Availability == SurroundAvailability.Available
            ? Loc.Instance["Editor_SurroundNoGrid"]
            : Loc.Instance["Editor_SurroundNoDriver"];
        SurroundHintIsError = wantsOn;
    }

    private SurroundSetting? BuildSurround() => SelectedSurround?.Key switch
    {
        SurroundOff => new SurroundSetting { Enabled = false },
        SurroundOn when _surroundGrid is { } grid => new SurroundSetting { Enabled = true, Grid = grid },
        _ => null,
    };

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
        DesktopIcons = DesktopIcons is { IsEmpty: false } ? DesktopIcons : null,
        Surround = BuildSurround(),
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

/// <summary>One display of the profile: chosen in the picture, edited in the card under it.</summary>
public sealed partial class DisplayEditItem : ObservableObject
{
    private readonly ProfileEditorViewModel _owner;
    private bool _syncing;

    public DisplayEditItem(ProfileEditorViewModel owner, DisplayAssignment assignment)
    {
        _owner = owner;
        Assignment = assignment;
        CustomName = assignment.CustomName ?? string.Empty;
        Sync(assignment);
    }

    public DisplayAssignment Assignment { get; private set; }

    /// <summary>The device path; what the picture reports as the selected key.</summary>
    public string Key => Assignment.Identity.TargetDevicePath;

    /// <summary>"Name · Model", as the switch messages call it.</summary>
    public string Name => SwitchMessages.NameOf(Assignment);

    /// <summary>The monitor as Windows calls it, under the name in the card.</summary>
    public string ModelName => DisplayNames.Of(Assignment.Identity);

    [ObservableProperty]
    public partial string ModeText { get; private set; } = string.Empty;

    /// <summary>"3840 × 2160", under the model name in the card; the position is in the picture.</summary>
    public string ResolutionText => string.Create(Loc.Instance.Culture, $"{Assignment.Width} × {Assignment.Height}");

    /// <summary>The user's name for this monitor; saved with the profile and carried to every profile with the same monitor.</summary>
    [ObservableProperty]
    public partial string CustomName { get; set; }

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
        OnPropertyChanged(nameof(Name));
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

    partial void OnCustomNameChanged(string value)
    {
        if (!_syncing)
        {
            Assignment = Assignment with { CustomName = DisplayNames.Normalize(value) };
            OnPropertyChanged(nameof(Name));
            _owner.OnDisplaysEdited();
        }
    }

    partial void OnSelectedRefreshChanged(RefreshChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { RefreshNumerator = value.Rate.Numerator, RefreshDenominator = value.Rate.Denominator };
            _owner.OnDisplaysEdited();
        }
    }

    partial void OnSelectedHdrChanged(HdrChoice? value)
    {
        if (!_syncing && value is not null)
        {
            Assignment = Assignment with { Hdr = value.Value };
            _owner.OnDisplaysEdited();
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
            _owner.OnDisplaysEdited();
        }
    }
}

/// <summary>One app entry in the editor.</summary>
public sealed partial class AppEditItem : ObservableObject
{
    /// <param name="showWhen">
    /// Offer "before / after the game". Only the game editor does: a profile has no game to be before or after.
    /// </param>
    public AppEditItem(AppAction action, bool showWhen = false)
    {
        ArgumentNullException.ThrowIfNull(action);

        ShowWhen = showWhen;
        FillKindChoices();
        FillWhenChoices();
        SelectedKind = KindChoices[action.Kind == AppActionKind.Stop ? 1 : 0];
        SelectedWhen = WhenChoices[action.When == AppTiming.AfterGame ? 1 : 0];
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
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>The picked name, else the file name: the row's first line.</summary>
    public string DisplayName =>
        _pickedName is not null && string.Equals(Path.Trim(), _pickedPath, StringComparison.OrdinalIgnoreCase)
            ? _pickedName
            : System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } file ? file : Loc.Instance["App_Path"];

    public ObservableCollection<Choice> KindChoices { get; } = [];

    /// <summary>Null only for a moment while the list is rebuilt; that counts as "start".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(PathNote), nameof(HasPathNote))]
    public partial Choice? SelectedKind { get; set; }

    /// <summary>Arguments only apply when starting.</summary>
    public bool IsStart => SelectedKind?.Key != nameof(AppActionKind.Stop);

    public ObservableCollection<Choice> WhenChoices { get; } = [];

    /// <summary>Only shown in the game editor; a profile ignores it.</summary>
    public bool ShowWhen { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAfterGame))]
    public partial Choice? SelectedWhen { get; set; }

    public bool IsAfterGame => SelectedWhen?.Key == nameof(AppTiming.AfterGame);

    /// <summary>New texts after a language change, same selection.</summary>
    internal void Relabel()
    {
        bool start = IsStart;
        bool after = IsAfterGame;
        FillKindChoices();
        FillWhenChoices();
        SelectedKind = KindChoices[start ? 0 : 1];
        SelectedWhen = WhenChoices[after ? 1 : 0];
        OnPropertyChanged(nameof(PathNote));
    }

    public bool HasPathNote => PathNote is not null;

    private void FillKindChoices()
    {
        KindChoices.Clear();
        KindChoices.Add(new Choice(nameof(AppActionKind.Start), Loc.Instance["App_Start"]));
        KindChoices.Add(new Choice(nameof(AppActionKind.Stop), Loc.Instance["App_Stop"]));
    }

    private void FillWhenChoices()
    {
        WhenChoices.Clear();
        WhenChoices.Add(new Choice(nameof(AppTiming.BeforeGame), Loc.Instance["App_BeforeGame"]));
        WhenChoices.Add(new Choice(nameof(AppTiming.AfterGame), Loc.Instance["App_AfterGame"]));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Icon), nameof(DisplayName), nameof(PathNote), nameof(HasPathNote))]
    public partial string Path { get; set; }

    /// <summary>
    /// What is wrong with the path, in words, shown before it; <c>null</c> when nothing is. An empty path is the
    /// editor's own problem line, and stopping by process name needs no file.
    /// </summary>
    public string? PathNote
    {
        get
        {
            string path = Path.Trim();
            if (path.Length == 0)
            {
                return null;
            }

            if (!LaunchPath.IsFullyQualified(path))
            {
                return IsStart ? Loc.Instance["Restore_WarnNotFullPath"] : null;
            }

            return File.Exists(LaunchPath.Expand(path)) ? null : Loc.Instance["App_NotFound"];
        }
    }

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
        When = IsAfterGame ? AppTiming.AfterGame : AppTiming.BeforeGame,
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
