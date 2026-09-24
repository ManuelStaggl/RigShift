using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Topology;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The detail of one profile on the profiles page: seeing and editing are the same view (R-NAV-3). Every change is
/// validated at once; the save bar shows the problem count, and saving writes the profile and its USB rules together.
/// Resolutions and positions are not editable; they come from "use current arrangement" (docs/display-topology.md).
/// </summary>
public sealed partial class ProfileEditorViewModel : ObservableObject, IDetailEditor, IHotkeyField
{
    private readonly HotkeyRecorder _hotkeyRecorder;
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
    private readonly SettingsService _settings;
    private readonly ILogger _log;

    public ProfileEditorViewModel(Profile profile, bool isNew, ProfileEditorContext context, ProfileEditorServices services)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(services);

        _original = profile;
        Saved = profile;
        _catalog = services.Catalog;
        _display = services.Display;
        _settings = services.Settings;
        IsNew = isNew;
        ConfirmationEnabled = context.ConfirmationEnabled;
        Hotkey = profile.Hotkey;
        _hotkeyRecorder = new HotkeyRecorder(services.Hotkeys, HotkeyUseKind.Profile, profile.Id);
        Rules = context.Rules;
        _log = services.Log.ForContext<ProfileEditorViewModel>();

        Name = profile.Name;
        FillIconChoices(ProfileIcons.Normalize(profile.Icon));
        SwitchWithoutAsking = profile.SwitchWithoutAsking;
        SetDisplays(profile.Displays);

        AudioAssignment audio = profile.Audio;
        AudioSlots =
        [
            new AudioSlot("Audio_Playback", "Audio_Unchanged", context.PlaybackDevices, audio.Playback, audio.PlaybackVolumePercent, supportsVolume: true),
            new AudioSlot("Audio_Recording", "Audio_Unchanged", context.RecordingDevices, audio.Recording, audio.RecordingVolumePercent, supportsVolume: true),
        ];
        CommunicationsAudioSlots =
        [
            new AudioSlot("Audio_PlaybackComms", "Audio_SameAsPlayback", context.PlaybackDevices, audio.PlaybackCommunications),
            new AudioSlot("Audio_RecordingComms", "Audio_SameAsRecording", context.RecordingDevices, audio.RecordingCommunications),
        ];
        ShowCommunicationsAudio = audio.PlaybackCommunications is not null || audio.RecordingCommunications is not null;
        foreach (AudioSlot slot in AudioSlots.Concat(CommunicationsAudioSlots))
        {
            slot.PropertyChanged += OnPartChanged;
        }

        AppList = new AppListEditor(profile.Apps, showWhen: false, context.AppsWaitDevice, services.AppPicker, "App_Path");

        _desktopIcons = services.DesktopIcons;
        DesktopIcons = profile.DesktopIcons;
        KeepAwake = profile.KeepAwake;
        DisableCommunicationsDucking = profile.DisableCommunicationsDucking;
        FillSurroundChoices(context.Surround, profile.Surround);

        // The editor's own reading of the profile, so defaults it fills in do not count as changes.
        _initial = Build();
        _loading = false;

        Displays.CollectionChanged += OnDisplaysChanged;
        AppList.Changed += OnPartChanged;
        Rules.Changed += OnPartChanged;

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

    /// <summary>The programs the profile starts and ends, and the USB device they wait for.</summary>
    public AppListEditor AppList { get; }

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

    /// <summary>The line under the hotkey field: how to record one, or why the last combination was refused.</summary>
    public string HotkeyHint => _hotkeyRecorder.Hint;

    [ObservableProperty]
    public partial string? ArrangementNote { get; private set; }

    /// <summary>Recording a hotkey RigShift holds would switch right away, so they rest while the field has the focus.</summary>
    public void BeginHotkeyRecording() => _hotkeyRecorder.Begin();

    public void EndHotkeyRecording() => _hotkeyRecorder.End();

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
    public void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
    {
        if (_hotkeyRecorder.Record(modifiers, virtualKey) is { } hotkey)
        {
            Hotkey = hotkey;
            _log.Information("Editor recorded hotkey {Hotkey}", HotkeyText);
        }

        OnPropertyChanged(nameof(HotkeyHint));
    }

    [RelayCommand]
    public void ClearHotkey()
    {
        Hotkey = null;
        _hotkeyRecorder.Reset();
        OnPropertyChanged(nameof(HotkeyHint));
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
            DisplaySnapshot snapshot = await _display.QueryAsync(CancellationToken.None);
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
                IReadOnlyList<RefreshRate> rates =
                    await _display.ListRefreshRatesAsync(assignment.Identity, assignment.Width, assignment.Height, CancellationToken.None);
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
            catch (Exception ex) when (DisplayApiFailure.Is(ex))
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
        AppList.Changed -= OnPartChanged;
        AppList.Dispose();
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

    private void OnPartChanged(object? sender, EventArgs e) => Recalculate();

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (EditorFields<ProfileEditorViewModel>.Contains(e.PropertyName))
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
        NameProblem = ProblemTexts.Of(problems, ProfileProblem.NameMissing, ProfileProblem.NameTooLong, ProfileProblem.NameTaken);
        DisplaysProblem = ProblemTexts.Of(problems, ProfileProblem.NoDisplays, ProfileProblem.NoSinglePrimary, ProfileProblem.PrimaryIsOptional);
        HotkeyProblem = ProblemTexts.Of(problems, ProfileProblem.HotkeyInvalid, ProfileProblem.HotkeyTaken);
        AppsProblem = ProblemTexts.Of(problems, ProfileProblem.AppPathMissing);
        AppList.Problem = AppsProblem;
        IsDirty = IsNew || Rules.IsDirty || !StoredForm.Same(built, _initial);
        UpdateSurroundHint();
    }

    private void UpdateTopology() => TopologyDisplays = Services.TopologyDisplays.From(Displays.Select(d => d.Assignment), _missingDisplays);

    /// <summary>
    /// Rebuilds the texts made in code and keeps every selection by key. One-off messages (errors, "took N displays")
    /// are cleared rather than translated; they come back with the next action.
    /// </summary>
    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HotkeyHint));
        OnPropertyChanged(nameof(HotkeyText));
        OnPropertyChanged(nameof(DesktopIconsText));
        ErrorMessage = null;
        ArrangementNote = null;

        _loading = true;
        try
        {
            FillIconChoices(SelectedIcon?.Key);
            AppList.Relabel();
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
        Apps = AppList.Build(),
        AppsWaitForUsbDeviceId = AppList.WaitDevice.DeviceId,
        AppsWaitForUsbDeviceName = AppList.WaitDevice.DeviceName,
        KeepAwake = KeepAwake,
        DisableCommunicationsDucking = DisableCommunicationsDucking,
        DesktopIcons = DesktopIcons is { IsEmpty: false } ? DesktopIcons : null,
        Surround = BuildSurround(),
    };
}
