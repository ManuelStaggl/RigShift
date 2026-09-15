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
    private readonly ILogger _log;

    /// <summary>Devices seen so far on the trigger step; a device outside this set has just been turned on.</summary>
    private readonly HashSet<string> _seenDevices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _windowsNames = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<DisplayAssignment> _currentDisplays = [];
    private bool _hasUsbBaseline;
    private bool _polling;

    public SetupWizardViewModel(
        ProfileCatalog catalog,
        IDisplayConfigurator display,
        IAudioController audio,
        IUsbDeviceList usb,
        IUsbPowerCheck powerCheck,
        ActiveProfileMatcher matcher,
        SettingsService settings,
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
        _log = log.ForContext<SetupWizardViewModel>();
        ProfileName = string.Empty;
    }

    /// <summary>Raised when the assistant is finished or skipped.</summary>
    public event EventHandler? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWelcome), nameof(IsProfileStep), nameof(IsSecond), nameof(IsTrigger), nameof(IsDone), nameof(StepTitle), nameof(StepText), nameof(StepCounter), nameof(RuleSummary))]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial SetupStep Step { get; private set; }

    public bool IsWelcome => Step == SetupStep.Welcome;

    public bool IsProfileStep => Step is SetupStep.First or SetupStep.Second;

    public bool IsSecond => Step == SetupStep.Second;

    public bool IsTrigger => Step == SetupStep.Trigger;

    public bool IsDone => Step == SetupStep.Done;

    public string StepTitle => Loc.Instance[$"Setup_{Step}Title"];

    public string StepText => Step switch
    {
        SetupStep.Second => Loc.Format("Setup_SecondText", FirstProfile?.Name),
        SetupStep.Done => Loc.Instance[CreatedRule is null ? "Setup_DoneNoRule" : "Setup_DoneWithRule"],
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

    // Cast to nullable: FirstOrDefault on the enum itself would turn "no name problem" into NameMissing (value 0).
    public string? NameProblem => ProfileEditing.Validate(new Profile { Id = Guid.Empty, Name = ProfileName, Displays = _currentDisplays }, _catalog.Profiles)
        .Select(p => (ProfileProblem?)p)
        .FirstOrDefault(p => p is ProfileProblem.NameMissing or ProfileProblem.NameTaken or ProfileProblem.NameTooLong) switch
    {
        ProfileProblem.NameMissing => Loc.Instance["Problem_NameMissing"],
        ProfileProblem.NameTaken => Loc.Instance["Problem_NameTaken"],
        ProfileProblem.NameTooLong => Loc.Instance["Problem_NameTooLong"],
        _ => null,
    };

    public bool HasNameProblem => NameProblem is not null;

    /// <summary>The active displays, left to right, as the profile cards show them.</summary>
    public ObservableCollection<string> DisplayLines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    [NotifyPropertyChangedFor(nameof(HasNoDisplays))]
    public partial bool HasDisplays { get; private set; }

    public bool HasNoDisplays => !HasDisplays;

    /// <summary>On the second step: Windows still shows the first profile's arrangement, so both would be the same.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand))]
    public partial bool MatchesFirst { get; private set; }

    [ObservableProperty]
    public partial AudioSlot? Playback { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveProfileCommand), nameof(CreateRuleCommand))]
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
        await EnterProfileStepAsync(SetupStep.First, Loc.Instance["Setup_FirstName"]);
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

        HasDisplays = _currentDisplays.Count > 0;
        MatchesFirst = Step == SetupStep.Second && FirstProfile is { } first && HasDisplays && _matcher.FindActive([first], snapshot) is not null;
        OnPropertyChanged(nameof(NameProblem));
        OnPropertyChanged(nameof(HasNameProblem));
        SaveProfileCommand.NotifyCanExecuteChanged();

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
                Icon = Step == SetupStep.First ? ProfileIcons.Desk : ProfileIcons.Rig,
            };

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
                await EnterProfileStepAsync(SetupStep.Second, Loc.Instance["Setup_SecondName"]);
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

    private async Task EnterProfileStepAsync(SetupStep step, string baseName)
    {
        Step = step;
        ProfileName = ProfileEditing.UniqueName(baseName, _catalog.Profiles.Select(p => p.Name));
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
            Summary.Add(new ProfileItem(profile, _settings.Current.UsbDeviceNames));
        }

        Step = SetupStep.Done;
    }

    /// <summary>The saved profiles, shown on the last step.</summary>
    public ObservableCollection<ProfileItem> Summary { get; } = [];

    /// <summary>What the created rule does, on the last step; <c>null</c> without a rule.</summary>
    public string? RuleSummary => CreatedRule is not null ? RuleText : null;

#if DEBUG
    /// <summary>Developer aid: a later step with demo profiles made from the current arrangement; nothing is saved.</summary>
    internal async Task PreviewAsync(SetupStep step)
    {
        DisplaySnapshot snapshot = await QueryAsync();
        FirstProfile = ProfileEditing.Capture(Loc.Instance["Setup_FirstName"], snapshot);
        SecondProfile = ProfileEditing.Capture(Loc.Instance["Setup_SecondName"], snapshot);
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
            devices = await Task.Run(() => _audio.ListAsync(AudioDirection.Render, CancellationToken.None));
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
            return await Task.Run(() => _display.QueryAsync(CancellationToken.None));
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "Setup assistant could not read the current arrangement");
            return new DisplaySnapshot { TakenAt = DateTimeOffset.Now, Displays = [] };
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
