using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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

public enum SetupStep
{
    Welcome,
    First,
    Second,
    Trigger,
    Done,
}

/// <summary>One numbered line: an entry of the assistant's step list, or one of the three lines on the welcome step.</summary>
/// <param name="IsDone">Behind the current step: the circle is filled and carries a check mark.</param>
/// <param name="IsCurrent">The step the assistant is on: accent ring, bold label.</param>
/// <param name="IsAhead">Still to come: number and label are dimmed. The welcome lines set none of the three.</param>
public sealed record WizardStepItem(int Number, string Text, bool IsDone, bool IsCurrent, bool IsAhead);

/// <summary>
/// Setup assistant for first-time users: the first profile from the current
/// arrangement, the second after the user has switched their displays, then an optional USB trigger between them.
/// Details such as HDR, apps and hotkeys stay in the profile editor.
/// </summary>
public sealed partial class SetupWizardViewModel : ObservableObject
{
    private readonly ProfileCatalog _catalog;
    private readonly IDisplayConfigurator _display;
    private readonly IAudioController _audio;
    private readonly IUsbDeviceList _usb;
    private readonly IUsbPowerCheck _powerCheck;
    private readonly ActiveProfileMatcher _matcher;
    private readonly SettingsService _settings;
    private readonly ISurroundController _surround;
    private readonly HotkeyService _hotkeys;
    private readonly ILogger _log;

    /// <summary>
    /// The hotkeys the first and the second profile get when they are free (finding U-02). Function keys, because
    /// Ctrl+Alt+digit is AltGr+digit on many layouts and would swallow ², ³, { and [ (German) or # (French).
    /// </summary>
    private static readonly Hotkey[] SuggestedHotkeys =
    [
        new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 },
        new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x71 },
    ];

    /// <summary>Resource key of <see cref="SurroundHint"/>; <c>null</c> says nothing.</summary>
    private string? _surroundHintKey;

    /// <summary>Devices seen so far on the trigger step; a device outside this set has just been turned on.</summary>
    private readonly HashSet<string> _seenDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _windowsNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DisplayAssignment> _currentDisplays = [];
    private bool _hasUsbBaseline;
    private bool _polling;

    /// <summary>The profile this step overwrites when the user came back to it; <c>null</c> while the step is new.</summary>
    private Guid? _editingId;

    public SetupWizardViewModel(
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IAudioController audio,
        IUsbDeviceList usb,
        IUsbPowerCheck powerCheck,
        ActiveProfileMatcher matcher,
        SettingsService settings,
        ISurroundController surround,
        HotkeyService hotkeys,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _catalog = catalog;
        _display = display;
        _audio = audio;
        _usb = usb;
        _powerCheck = powerCheck;
        _matcher = matcher;
        _settings = settings;
        _surround = surround;
        _hotkeys = hotkeys;
        _log = log.ForContext<SetupWizardViewModel>();
        ProfileName = string.Empty;
        RebuildLists();

        // A method group: the weak event manager holds the handler's target weakly, a lambda's closure would be collected.
        PropertyChangedEventManager.AddHandler(Loc.Instance, OnLanguageChanged, string.Empty);
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e)
    {
        RebuildLists();
        OnPropertyChanged(nameof(SurroundHint));
    }

    /// <summary>The two numbered lists; both carry translated text, so they are built, not bound to resources.</summary>
    private void RebuildLists()
    {
        StepList.Clear();
        for (int i = 0; i < 5; i++)
        {
            var step = (SetupStep)i;
            StepList.Add(new WizardStepItem(i + 1, Loc.Instance[$"Setup_Step{step}"], i < (int)Step, step == Step, i > (int)Step));
        }

        WelcomeList.Clear();
        for (int i = 1; i <= 3; i++)
        {
            WelcomeList.Add(new WizardStepItem(i, Loc.Instance[$"Setup_WelcomeStep{i}"], false, false, false));
        }
    }

    /// <summary>Raised when the assistant is finished or skipped.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsProfileStep), nameof(IsSecond), nameof(IsTrigger), nameof(IsDone), nameof(ShowTry), nameof(StepTitle), nameof(StepText), nameof(StepCounter), nameof(RuleSummary), nameof(HasRuleSummary), nameof(CanGoBack), nameof(DifferentText), nameof(SurroundHint), nameof(HasSurroundHint))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand), nameof(BackCommand))]
    public partial SetupStep Step { get; private set; }

    /// <summary>The five entries of the step list on the left, in order, with the current one marked.</summary>
    public ObservableCollection<WizardStepItem> StepList { get; } = [];

    /// <summary>The three numbered lines of the welcome step; plain numbers, no state.</summary>
    public ObservableCollection<WizardStepItem> WelcomeList { get; } = [];

    public bool IsWelcome => Step == SetupStep.Welcome;

    public bool IsProfileStep => Step is SetupStep.First or SetupStep.Second;

    public bool IsSecond => Step == SetupStep.Second;

    public bool IsTrigger => Step == SetupStep.Trigger;

    public bool IsDone => Step == SetupStep.Done;

    public string StepTitle => Loc.Instance[$"Setup_{Step}Title"];

    public string StepText => Step switch
    {
        SetupStep.Second => Loc.Format("Setup_SecondText", FirstProfile?.Name),
        // Names only the ways that really exist: the hotkeys are on the cards below, when they were free (U-02).
        SetupStep.Done => Loc.Instance[(CreatedRule is null ? "Setup_DoneNoRule" : "Setup_DoneWithRule") + (HasHotkeys ? string.Empty : "NoHotkey")],
        _ => Loc.Instance[$"Setup_{Step}Text"],
    };

    /// <summary>"Step 2 of 3" on the three working steps, empty on welcome and done.</summary>
    public string StepCounter => Step is SetupStep.First or SetupStep.Second or SetupStep.Trigger
        ? Loc.Format("Setup_StepCounter", (int)Step, 3)
        : string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(NameProblem), nameof(HasNameProblem))]
    public partial string ProfileName { get; set; }

    public string? NameProblem => ProblemTexts.Of(
        ProfileEditing.Validate(new Profile { Id = _editingId ?? Guid.Empty, Name = ProfileName, Displays = _currentDisplays }, _catalog.Profiles),
        ProfileProblem.NameMissing, ProfileProblem.NameTooLong, ProfileProblem.NameTaken);

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>The active displays, left to right, as the profile cards show them.</summary>
    public ObservableCollection<string> DisplayLines { get; } = [];

    /// <summary>The same arrangement as the picture draws it (spec 7.5, size M).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TopologyDisplay> Topology { get; private set; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(HasNoDisplays), nameof(IsDifferent))]
    public partial bool HasDisplays { get; private set; }

    public bool HasNoDisplays => !HasDisplays;

    /// <summary>On the second step: Windows still shows the first profile's arrangement, so both would be the same.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(IsDifferent))]
    public partial bool MatchesFirst { get; private set; }

    /// <summary>The counterpart of <see cref="MatchesFirst"/>: the second arrangement differs, so it can be saved.</summary>
    public bool IsDifferent => IsSecond && HasDisplays && !MatchesFirst;

    public string DifferentText => Loc.Format("Setup_DifferentText", FirstProfile?.Name);

    /// <summary>
    /// One line on NVIDIA Surround for the profile steps: that the running grid comes along, or on the second step how
    /// to get one in (finding U-05). Empty without an NVIDIA driver.
    /// </summary>
    public string SurroundHint => HasSurroundHint ? Loc.Instance[_surroundHintKey!] : string.Empty;

    public bool HasSurroundHint => IsProfileStep && _surroundHintKey is not null;

    [ObservableProperty]
    public partial AudioSlot? Playback { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand), nameof(CreateRuleCommand), nameof(BackCommand))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    public partial bool IsBusy { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    public bool HasError => ErrorMessage is not null;

    public Profile? FirstProfile { get; private set; }

    public Profile? SecondProfile { get; private set; }

    /// <summary>Connected USB devices for the trigger.</summary>
    public ObservableCollection<Choice> DeviceChoices { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CreateRuleCommand))]
    [NotifyPropertyChangedFor(nameof(RuleText))]
    public partial Choice? SelectedDevice { get; set; }

    /// <summary>"Detected: Wheelbase" once a device was turned on while the step was open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDetected))]
    public partial string? DetectedMessage { get; private set; }

    public bool HasDetected => DetectedMessage is not null;

    /// <summary>Windows may power the chosen device down (hint only, docs/usb-power-saving.md).</summary>
    [ObservableProperty]
    public partial bool HasPowerWarning { get; private set; }

    /// <summary>What the rule will do, in words.</summary>
    public string? RuleText => SelectedDevice is { } device && FirstProfile is { } first && SecondProfile is { } second
        ? Loc.Format("Setup_RuleText", device.Name, second.Name, first.Name)
        : null;

    public AutomationRule? CreatedRule { get; private set; }

    [RelayCommand]
    private async Task StartAsync()
    {
        _log.Information("Setup assistant started with {Count} existing profile(s)", _catalog.Profiles.Count);
        await EnterProfileStepAsync(SetupStep.First, Loc.Instance["Setup_FirstName"], FirstProfile);
    }

    /// <summary>Every step but the first can be left backwards; what a step already saved is then edited, not doubled.</summary>
    public bool CanGoBack => Step != SetupStep.Welcome && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private async Task BackAsync()
    {
        switch (Step)
        {
            case SetupStep.First:
                Step = SetupStep.Welcome;
                break;
            case SetupStep.Second:
                await EnterProfileStepAsync(SetupStep.First, Loc.Instance["Setup_FirstName"], FirstProfile);
                break;
            case SetupStep.Trigger:
                await EnterProfileStepAsync(SetupStep.Second, Loc.Instance["Setup_SecondName"], SecondProfile);
                break;
            case SetupStep.Done:
                await RemoveCreatedRuleAsync();
                EnterTrigger();
                break;
        }

        _log.Information("Setup assistant went back to step {Step}", Step);
    }

    /// <summary>Going back past the rule undoes it: the trigger step would otherwise offer to create a second one.</summary>
    private async Task RemoveCreatedRuleAsync()
    {
        if (CreatedRule is not { } rule)
        {
            return;
        }

        try
        {
            await _settings.UpdateAsync(
                s => s with { AutomationRules = [.. (s.AutomationRules ?? []).Where(r => r.Id != rule.Id)] },
                CancellationToken.None);
            CreatedRule = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Setup assistant could not remove the automation rule it created");
        }
    }

    [RelayCommand]
    private void Skip()
    {
        _log.Information("Setup assistant closed at step {Step}", Step);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void OpenDisplaySettings()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo { FileName = "ms-settings:display", UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _log.Warning(ex, "Windows display settings could not be opened");
        }
    }

    /// <summary>Reads the active displays again; the window calls it after every display change.</summary>
    public async Task RefreshDisplaysAsync()
    {
        if (!IsProfileStep)
        {
            return;
        }

        DisplaySnapshot snapshot = await QueryAsync();
        _currentDisplays = ProfileEditing.CurrentArrangement(snapshot, [], _catalog.KnownDisplayNames);
        var preview = new Profile { Id = Guid.Empty, Name = "-", Displays = _currentDisplays };
        DisplayLines.Clear();
        foreach (string line in new ProfileItem(preview).DisplayLines)
        {
            DisplayLines.Add(line);
        }

        Topology = Services.TopologyDisplays.From(_currentDisplays);
        HasDisplays = _currentDisplays.Count > 0;
        MatchesFirst = Step == SetupStep.Second && FirstProfile is { } first && HasDisplays && _matcher.FindActive([first], snapshot) is not null;
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        SaveProfileCommand.NotifyCanExecuteChanged();

        // Switching Surround on or off changes the displays, so this runs again after it.
        SurroundState surround = await ReadSurroundAsync();
        _surroundHintKey = surround.Availability != SurroundAvailability.Available ? null
            : surround.IsActive ? "Setup_SurroundOn"
            : Step == SetupStep.Second ? "Setup_SurroundHint"
            : null;
        OnPropertyChanged(nameof(SurroundHint));
        OnPropertyChanged(nameof(HasSurroundHint));

        // A monitor's own speakers appear with the monitor; the choice made so far stays if the device is still there.
        await FillPlaybackAsync(Playback?.Endpoint);
        _log.Information("Setup assistant sees {Count} active display(s) at step {Step}, same as first profile: {Same}", _currentDisplays.Count, Step, MatchesFirst);
    }

    [RelayCommand(CanExecute = nameof(CanSaveProfile))]
    private async Task SaveProfileAsync()
    {
        IsBusy = true;
        try
        {
            DisplaySnapshot snapshot = await QueryAsync();
            Profile profile = ProfileEditing.Capture(ProfileName, snapshot, new AudioAssignment { Playback = Playback?.Endpoint }, _catalog.KnownDisplayNames) with
            {
                // Coming back to a step overwrites what it saved before instead of leaving a second profile behind.
                Id = _editingId ?? Guid.NewGuid(),
                Icon = Step == SetupStep.First ? ProfileIcons.Desk : ProfileIcons.Rig,
            };

            // A running grid is part of the arrangement, as in "From the current arrangement": without it the profile could
            // never bring the wide display back (finding U-05). The other profile needs nothing - without a setting of
            // its own it switches Surround off once this one uses it.
            if (await ReadSurroundAsync() is { IsActive: true } surround)
            {
                profile = profile with { Surround = new SurroundSetting { Enabled = true, Grid = surround.Grids[0] } };
            }

            profile = profile with { Hotkey = HotkeyFor(profile.Id, Step == SetupStep.First ? 0 : 1) };

            if (ProfileEditing.Validate(profile, _catalog.Profiles).Count > 0
                || (Step == SetupStep.Second && FirstProfile is { } first && _matcher.FindActive([first], snapshot) is not null))
            {
                // The arrangement changed between the last refresh and the click; show what is true now.
                await RefreshDisplaysAsync();
                return;
            }

            await _catalog.SaveAsync(profile, CancellationToken.None);
            ErrorMessage = null;
            _log.Information("Setup assistant saved profile {Profile} with {Count} display(s)", profile.Name, profile.Displays.Count);

            if (Step == SetupStep.First)
            {
                FirstProfile = profile;

                // The first arrangement becomes the default profile (F1): it is the one the user comes back to.
                // Only when nothing is set yet – an assistant run on an existing setup must not take that over.
                if (_settings.Current.DefaultProfileId is null)
                {
                    await _catalog.ToggleDefaultAsync(profile, CancellationToken.None);
                }

                await EnterProfileStepAsync(SetupStep.Second, Loc.Instance["Setup_SecondName"], SecondProfile);
            }
            else
            {
                SecondProfile = profile;
                EnterTrigger();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Setup assistant could not save a profile");
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanSaveProfile() => IsProfileStep && !IsBusy && HasDisplays && !MatchesFirst && NameProblem is null;

    /// <summary>
    /// The hotkey of a step's profile: the one it had when the user came back to the step, else the suggestion when
    /// nothing in RigShift and no other application holds it. <c>null</c> leaves the profile without one.
    /// </summary>
    private Hotkey? HotkeyFor(Guid id, int index)
    {
        if (_catalog.Profiles.FirstOrDefault(p => p.Id == id)?.Hotkey is { } kept)
        {
            return kept;
        }

        Hotkey suggested = SuggestedHotkeys[index];
        if (_hotkeys.UsedBy(suggested, HotkeyUseKind.Profile, id) is not null || !_hotkeys.IsAvailable(suggested))
        {
            _log.Information("Setup assistant leaves the hotkey out: {Hotkey} is taken", HotkeyFormat.Format(suggested));
            return null;
        }

        return suggested;
    }

    /// <summary>One look at the USB devices; the window calls it every 2 seconds while the trigger step is open.</summary>
    public void PollUsb()
    {
        if (!IsTrigger || _polling)
        {
            return;
        }

        _polling = true;
        try
        {
            IReadOnlyList<UsbDevice> connected = ListUsb();
            string? keep = SelectedDevice?.Key;
            List<UsbDevice> turnedOn = connected.Where(d => !_seenDevices.Contains(d.Id)).ToList();
            bool first = !_hasUsbBaseline;
            _hasUsbBaseline = true;
            foreach (UsbDevice device in connected)
            {
                _seenDevices.Add(device.Id);
                _windowsNames[device.Id] = device.Name;
            }

            IReadOnlyDictionary<string, string>? custom = _settings.Current.UsbDeviceNames;
            DeviceChoices.Clear();
            foreach (UsbDevice device in connected.OrderBy(d => UsbDeviceNames.NameOf(d.Id, d.Name, custom), StringComparer.CurrentCultureIgnoreCase))
            {
                DeviceChoices.Add(new Choice(device.Id, UsbDeviceNames.NameOf(device.Id, device.Name, custom)));
            }

            // The first look is the baseline: everything connected then was on before the user turned anything on.
            if (!first && turnedOn.Count > 0)
            {
                keep = turnedOn[0].Id;
                DetectedMessage = Loc.Format("Setup_Detected", UsbDeviceNames.NameOf(turnedOn[0].Id, turnedOn[0].Name, custom));
                _log.Information("Setup assistant detected USB device {Device} ({Id})", turnedOn[0].Name, turnedOn[0].Id);
            }

            SelectedDevice = DeviceChoices.FirstOrDefault(c => string.Equals(c.Key, keep, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            _polling = false;
        }
    }

    partial void OnSelectedDeviceChanged(Choice? value)
    {
        HasPowerWarning = false;
        if (value?.Key is not { } id)
        {
            return;
        }

        try
        {
            HasPowerWarning = UsbPowerSaving.ShouldWarn(_powerCheck.Check(id));
        }
        catch (Exception ex) when (ex is Win32Exception or System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "USB power check for {Device} failed", id);
        }
    }

    [RelayCommand(CanExecute = nameof(CanCreateRule))]
    private async Task CreateRuleAsync()
    {
        if (SelectedDevice?.Key is not { } id || FirstProfile is not { } first || SecondProfile is not { } second)
        {
            return;
        }

        var rule = new AutomationRule
        {
            Devices = [new RuleDevice { Id = id, Name = _windowsNames.GetValueOrDefault(id) }],
            ProfileId = second.Id,
            OnExit = ExitAction.SwitchTo,
            ExitProfileId = first.Id,
        };

        IsBusy = true;
        try
        {
            await _settings.UpdateAsync(s => s with { AutomationRules = [.. s.AutomationRules ?? [], rule] }, CancellationToken.None);
            CreatedRule = rule;
            ErrorMessage = null;
            _log.Information("Setup assistant created automation rule {Rule}: {Device} switches to {Profile}, back to {ExitProfile}", rule.Id, id, second.Name, first.Name);
            EnterDone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Setup assistant could not save the automation rule");
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreateRule() => IsTrigger && !IsBusy && SelectedDevice is not null;

    [RelayCommand]
    private void SkipTrigger()
    {
        _log.Information("Setup assistant finished without an automation rule");
        EnterDone();
    }

    /// <param name="existing">The profile this step already saved, when the user came back to it.</param>
    private async Task EnterProfileStepAsync(SetupStep step, string baseName, Profile? existing = null)
    {
        Step = step;
        _editingId = existing?.Id;
        ProfileName = existing?.Name ?? ProfileEditing.UniqueName(baseName, _catalog.Profiles.Select(p => p.Name));
        Playback = null;
        await RefreshDisplaysAsync();
    }

    private void EnterTrigger()
    {
        Step = SetupStep.Trigger;
        _seenDevices.Clear();
        _hasUsbBaseline = false;
        DeviceChoices.Clear();
        DetectedMessage = null;
        PollUsb();
        OnPropertyChanged(nameof(RuleText));
    }

    private void EnterDone()
    {
        Summary.Clear();
        foreach (Profile profile in new[] { FirstProfile, SecondProfile }.OfType<Profile>())
        {
            // The card caption is the profile's state, as everywhere else in the app: "Default · Active".
            Summary.Add(new ProfileItem(profile, _settings.Current.UsbDeviceNames)
            {
                IsDefault = _settings.Current.DefaultProfileId == profile.Id,
                IsActive = _catalog.ActiveProfile?.Id == profile.Id,
            });
        }

        // Preset on (finding U-01): after the next restart hotkeys and the USB trigger only work while RigShift runs.
        StartWithWindows = true;
        OnPropertyChanged(nameof(HasHotkeys));
        OnPropertyChanged(nameof(TryText));
        OnPropertyChanged(nameof(CanTry));
        Step = SetupStep.Done;
    }

    /// <summary>The saved profiles, shown on the last step.</summary>
    public ObservableCollection<ProfileItem> Summary { get; } = [];

    /// <summary>Whether a saved profile got a hotkey; the last step's text only promises hotkeys then.</summary>
    public bool HasHotkeys => Summary.Any(item => item.Profile.Hotkey is not null);

    /// <summary>"Start with Windows" on the last step; applied by <see cref="FinishCommand"/> and <see cref="TryItCommand"/>.</summary>
    [ObservableProperty]
    public partial bool StartWithWindows { get; set; }

    /// <summary>
    /// Where "Try it" switches: the profile that is not showing now, i.e. back to the first after the rig was set up
    /// (finding U-02 – the one click that shows what RigShift does, with the countdown that takes it back).
    /// </summary>
    private Profile? TryTarget => _catalog.ActiveProfile?.Id == FirstProfile?.Id ? SecondProfile : FirstProfile;

    public string TryText => Loc.Format("Setup_TryIt", TryTarget?.Name);

    public bool CanTry => TryTarget is not null;

    public bool ShowTry => IsDone && CanTry;

    /// <summary>Set by "Try it": the profile to switch to once the assistant has closed.</summary>
    public Profile? SwitchAfterClose { get; private set; }

    [RelayCommand]
    private void TryIt()
    {
        if (TryTarget is not { } target)
        {
            return;
        }

        ApplyAutostart();
        SwitchAfterClose = target;
        _log.Information("Setup assistant finished with a try: switching to {Profile}", target.Name);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Finish()
    {
        ApplyAutostart();
        _log.Information("Setup assistant finished");
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyAutostart()
    {
        try
        {
            if (_settings.Autostart.IsEnabled != StartWithWindows)
            {
                _settings.Autostart.SetEnabled(StartWithWindows);
                _log.Information("Setup assistant turned start with Windows {State}", StartWithWindows ? "on" : "off");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            _log.Warning(ex, "Setup assistant could not change start with Windows");
        }
    }

    /// <summary>What the created rule does, on the last step; <c>null</c> without a rule.</summary>
    public string? RuleSummary => CreatedRule is not null ? RuleText : null;

    public bool HasRuleSummary => RuleSummary is not null;

    partial void OnStepChanged(SetupStep value) => RebuildLists();

#if DEBUG
    /// <summary>Developer aid: a later step with demo profiles made from the current arrangement; nothing is saved.</summary>
    internal async Task PreviewAsync(SetupStep step)
    {
        DisplaySnapshot snapshot = await QueryAsync();
        FirstProfile = ProfileEditing.Capture(Loc.Instance["Setup_FirstName"], snapshot) with { Hotkey = SuggestedHotkeys[0] };
        SecondProfile = ProfileEditing.Capture(Loc.Instance["Setup_SecondName"], snapshot) with { Hotkey = SuggestedHotkeys[1] };
        switch (step)
        {
            case SetupStep.Second:
                await EnterProfileStepAsync(SetupStep.Second, Loc.Instance["Setup_SecondName"]);
                break;
            case SetupStep.Trigger:
                EnterTrigger();
                if (DeviceChoices.Count > 0)
                {
                    SelectedDevice = DeviceChoices[0];
                    DetectedMessage = Loc.Format("Setup_Detected", DeviceChoices[0].Name);
                }

                break;
            case SetupStep.Done:
                // Through the trigger step, so the rule sentence on the last step has a device to name.
                EnterTrigger();
                SelectedDevice = DeviceChoices.FirstOrDefault();
                CreatedRule = new AutomationRule();
                EnterDone();
                break;
        }
    }
#endif

    private async Task FillPlaybackAsync(AudioEndpoint? keep)
    {
        IReadOnlyList<AudioDeviceInfo> devices;
        try
        {
            devices = await _audio.ListAsync(AudioDirection.Render, CancellationToken.None);
        }
        catch (COMException ex)
        {
            _log.Warning(ex, "Playback devices could not be listed for the setup assistant");
            devices = [];
        }

        AudioEndpoint? selected = keep is not null && devices.Any(d => string.Equals(d.Endpoint.EndpointId, keep.EndpointId, StringComparison.OrdinalIgnoreCase))
            ? keep
            : devices.FirstOrDefault(d => d.IsDefault && d.IsActive)?.Endpoint;
        Playback = new AudioSlot("Audio_Playback", "Audio_Unchanged", devices, selected);
    }

    private async Task<DisplaySnapshot> QueryAsync()
    {
        try
        {
            return await _display.QueryAsync(CancellationToken.None);
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Setup assistant could not read the current arrangement");
            return new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = [] };
        }
    }

    /// <summary>The Surround state; when it cannot be read, the assistant says nothing about Surround and saves none.</summary>
    private async Task<SurroundState> ReadSurroundAsync()
    {
        try
        {
            return await _surround.QueryAsync(CancellationToken.None);
        }
        catch (Exception ex) when (DisplayApiFailure.Is(ex))
        {
            _log.Warning(ex, "Setup assistant could not read the Surround state");
            return SurroundState.Unavailable(SurroundAvailability.Unknown, ex.Message);
        }
    }

    private IReadOnlyList<UsbDevice> ListUsb()
    {
        try
        {
            return _usb.ConnectedDevices();
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed for the setup assistant");
            return [];
        }
    }
}
