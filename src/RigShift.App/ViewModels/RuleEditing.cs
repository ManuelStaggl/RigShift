using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RigShift.App.Localization;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>What a rule card needs from the list it sits in: device names and choices, the profile choices, and a way to say it changed.</summary>
public interface IRuleOwner
{
    /// <summary>Connected USB devices, plus saved devices that are not connected right now.</summary>
    ObservableCollection<Choice> DeviceChoices { get; }

    ObservableCollection<Choice> ExitChoices { get; }

    Choice? DeviceChoiceFor(string? deviceId);

    /// <summary>The device's name for messages – its custom name, else Windows' name – without the "not connected" note.</summary>
    string? DeviceNameFor(string? deviceId);

    /// <summary>Windows' name, stored with the rule so a device that is not connected still has one.</summary>
    string? WindowsNameFor(string? deviceId);

    /// <summary>"Wheel + Pedals"; <c>null</c> without devices.</summary>
    string? DescribeDevices(IReadOnlyList<string> deviceIds);

    bool IsDeviceConnected(string? deviceId);

    Choice? ProfileChoiceFor(Guid id);

    /// <summary>Whether Windows may power the device down; checked once per device until the next refresh.</summary>
    bool HasPowerWarning(string? deviceId);

    void UpdateDuplicates();

    /// <summary>Called when a rule's devices change: a newly picked device can be named at once.</summary>
    void OnRuleDevicesChanged();

    void OnCardChanged();
}

/// <summary>The "when a device is gone" choices and their keys, shared by the automation page and the profile's trigger tab.</summary>
public static class RuleExits
{
    public const string StayKey = "stay";
    public const string BackKey = "back";
    public const string ToPrefix = "to:";

    public static void Fill(ObservableCollection<Choice> exits, IEnumerable<Profile> profiles, IEnumerable<Guid> missingProfiles)
    {
        ArgumentNullException.ThrowIfNull(exits);
        exits.Clear();
        exits.Add(new Choice(StayKey, Loc.Instance["Automation_ExitStay"]));
        exits.Add(new Choice(BackKey, Loc.Instance["Automation_ExitBack"]));
        foreach (Profile profile in profiles)
        {
            exits.Add(new Choice(ToPrefix + profile.Id.ToString("D"), Loc.Format("Automation_ExitTo", profile.Name)));
        }

        foreach (Guid missing in missingProfiles)
        {
            exits.Add(new Choice(ToPrefix + missing.ToString("D"), Loc.Format("Automation_ExitTo", Loc.Instance["Automation_MissingProfile"])));
        }
    }

    public static Choice? ChoiceFor(ObservableCollection<Choice> exits, AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(exits);
        ArgumentNullException.ThrowIfNull(rule);
        return rule.OnExit switch
        {
            ExitAction.SwitchBack => exits.FirstOrDefault(c => c.Key == BackKey),
            ExitAction.SwitchTo when rule.ExitProfileId is { } id => exits.FirstOrDefault(c => c.Key == ToPrefix + id.ToString("D")),
            _ => exits.FirstOrDefault(c => c.Key == StayKey),
        };
    }

    public static (ExitAction Action, Guid? Profile) From(Choice? choice) => choice?.Key switch
    {
        BackKey => (ExitAction.SwitchBack, null),
        { } key when key.StartsWith(ToPrefix, StringComparison.Ordinal) && Guid.TryParse(key[ToPrefix.Length..], out Guid id) => (ExitAction.SwitchTo, id),
        _ => (ExitAction.Stay, null),
    };

    /// <summary>
    /// Profiles a new rule starts with: its end action switches to the default profile (the desk), or to the first profile
    /// without a default – deterministic, unlike "switch back" (user decision O-08). The rule itself switches to the first
    /// other profile.
    /// </summary>
    public static (Guid Profile, Guid ExitProfile) NewRuleProfiles(IReadOnlyList<Profile> profiles, Guid? defaultProfileId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentOutOfRangeException.ThrowIfZero(profiles.Count);

        Profile exit = profiles.FirstOrDefault(p => p.Id == defaultProfileId) ?? profiles[0];
        Profile start = profiles.FirstOrDefault(p => p.Id != exit.Id) ?? exit;
        return (start.Id, exit.Id);
    }
}

/// <summary>One rule: "when these USB devices are connected, switch to that profile".</summary>
public sealed partial class RuleCard : ObservableObject
{
    private readonly IRuleOwner _owner;

    public RuleCard(IRuleOwner owner, AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rule);

        _owner = owner;
        Id = rule.Id;
        ProfileId = rule.ProfileId;
        SetDevices(rule);
        SelectedProfile = owner.ProfileChoiceFor(rule.ProfileId);
        SelectedExit = RuleExits.ChoiceFor(owner.ExitChoices, rule);
        SkipConfirmation = rule.SkipConfirmation;
        ExitDelaySeconds = rule.ExitDelaySeconds;

        // A rule without a device does nothing yet, so it starts open.
        IsExpanded = DeviceIds.Count == 0;
    }

    /// <summary>The collapsed card's line: "Wheel + Pedals → Sim Rig".</summary>
    public string Title => $"{DevicesText ?? Loc.Instance["Automation_NoDevice"]} → {SelectedProfile?.Name ?? "–"}";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Marks a collapsed card whose warnings are inside.</summary>
    public bool HasWarning => HasPowerWarning || HasDuplicateDevice;

    public Guid Id { get; }

    /// <summary>The profile the rule switches to; on the profile's trigger tab it is the profile itself.</summary>
    public Guid ProfileId { get; }

    public IRuleOwner Owner => _owner;

    /// <summary>One entry per device; a combination switches once all of them are connected (user decision U-02).</summary>
    public ObservableCollection<RuleDeviceSlot> Devices { get; } = [];

    public bool IsCombination => Devices.Count > 1;

    public string DevicesHeader => Loc.Instance[IsCombination ? "Automation_DevicesConnect" : "Automation_DeviceConnects"];

    /// <summary>"Wheel + Pedals", for screen readers and the delete question.</summary>
    public string? DevicesText => _owner.DescribeDevices(DeviceIds);

    public bool HasDevices => DeviceIds.Count > 0;

    internal IReadOnlyList<string> DeviceIds => Devices
        .Select(s => s.SelectedDevice?.Key)
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The device set, independent of order; <c>null</c> without devices.</summary>
    internal string? DeviceKey => DeviceIds.Count == 0 ? null : string.Join('+', DeviceIds.Order(StringComparer.OrdinalIgnoreCase));

    /// <summary>Devices not on the card yet – what "+ Device" offers.</summary>
    public IReadOnlyList<Choice> AvailableDevices
    {
        get
        {
            IReadOnlyList<string> taken = DeviceIds;
            return _owner.DeviceChoices.Where(c => c.Key is { } key && !taken.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
        }
    }

    [ObservableProperty]
    public partial Choice? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedExit { get; set; }

    [ObservableProperty]
    public partial bool SkipConfirmation { get; set; }

    [ObservableProperty]
    public partial double? ExitDelaySeconds { get; set; }

    /// <summary>Windows may power one of the chosen devices down (hint only, docs/usb-power-saving.md).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial bool HasPowerWarning { get; private set; }

    internal void UpdatePowerWarning() => HasPowerWarning = DeviceIds.Any(_owner.HasPowerWarning);

    /// <summary>Another rule watches the same devices (analysis finding C-05).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarning))]
    public partial bool HasDuplicateDevice { get; internal set; }

    /// <summary>Shows the rule's devices from the current device list; a rule without any gets one empty entry.</summary>
    internal void SetDevices(AutomationRule rule)
    {
        Devices.Clear();
        IEnumerable<string> ids = (rule.Devices ?? [])
            .Select(d => UsbDeviceIds.Normalize(d.Id))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase);
        foreach (string id in ids)
        {
            Devices.Add(new RuleDeviceSlot(this, _owner.DeviceChoiceFor(id)));
        }

        if (Devices.Count == 0)
        {
            Devices.Add(new RuleDeviceSlot(this, null));
        }

        OnDevicesShapeChanged();
    }

    /// <param name="device">The device to add; without one, the first device the card does not have yet.</param>
    [RelayCommand]
    private void AddDevice(Choice? device)
    {
        IReadOnlyList<Choice> available = AvailableDevices;
        Choice? next = device ?? (available.Count > 0 ? available[0] : null);
        // An empty first slot (rule without devices) takes the device instead of adding a second slot.
        if (Devices.Count == 1 && Devices[0].SelectedDevice is null)
        {
            Devices[0].SelectedDevice = next;
            return;
        }

        Devices.Add(new RuleDeviceSlot(this, next));
        OnDevicesShapeChanged();
        OnDevicesChanged();
    }

    [RelayCommand]
    private void RemoveDevice(RuleDeviceSlot? slot)
    {
        if (slot is null || !Devices.Remove(slot))
        {
            return;
        }

        if (Devices.Count == 0)
        {
            Devices.Add(new RuleDeviceSlot(this, null));
        }

        OnDevicesShapeChanged();
        OnDevicesChanged();
    }

    public AutomationRule ToRule()
    {
        (ExitAction onExit, Guid? exitProfile) = RuleExits.From(SelectedExit);
        return new AutomationRule
        {
            Id = Id,
            // An empty list keeps a rule without a chosen device; it watches nothing until one is picked.
            Devices = DeviceIds.Select(id => new RuleDevice { Id = id, Name = _owner.WindowsNameFor(id) }).ToList(),
            ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profile) ? profile : ProfileId,
            OnExit = onExit,
            ExitProfileId = exitProfile,
            SkipConfirmation = SkipConfirmation,
            ExitDelaySeconds = ExitDelaySeconds is { } seconds
                ? (int)Math.Clamp(Math.Round(seconds), 0, AutomationRule.MaxExitDelaySeconds)
                : AutomationRule.DefaultExitDelaySeconds,
        };
    }

    internal void OnDevicesChanged()
    {
        UpdatePowerWarning();
        OnPropertyChanged(nameof(DevicesText));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(AvailableDevices));
        OnPropertyChanged(nameof(Title));
        _owner.UpdateDuplicates();
        _owner.OnRuleDevicesChanged();
        _owner.OnCardChanged();
    }

    private void OnDevicesShapeChanged()
    {
        OnPropertyChanged(nameof(IsCombination));
        OnPropertyChanged(nameof(DevicesHeader));
        OnPropertyChanged(nameof(DevicesText));
        OnPropertyChanged(nameof(HasDevices));
        OnPropertyChanged(nameof(AvailableDevices));
        OnPropertyChanged(nameof(Title));
    }

    partial void OnSelectedProfileChanged(Choice? value)
    {
        OnPropertyChanged(nameof(Title));
        _owner.OnCardChanged();
    }

    partial void OnSelectedExitChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSkipConfirmationChanged(bool value) => _owner.OnCardChanged();

    partial void OnExitDelaySecondsChanged(double? value) => _owner.OnCardChanged();
}

/// <summary>One device of a rule: a choice in a list, or a chip with its connection state.</summary>
public sealed partial class RuleDeviceSlot : ObservableObject
{
    private readonly bool _ready;

    public RuleDeviceSlot(RuleCard card, Choice? device)
    {
        Card = card;
        SelectedDevice = device;
        _ready = true;
    }

    public RuleCard Card { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(IsConnected), nameof(HasDevice))]
    public partial Choice? SelectedDevice { get; set; }

    public bool HasDevice => SelectedDevice is not null;

    /// <summary>The chip text: the device's name without the "not connected" note.</summary>
    public string Name => Card.Owner.DeviceNameFor(SelectedDevice?.Key) ?? SelectedDevice?.Name ?? Loc.Instance["Automation_NoDevice"];

    public bool IsConnected => Card.Owner.IsDeviceConnected(SelectedDevice?.Key);

    partial void OnSelectedDeviceChanged(Choice? value)
    {
        if (_ready)
        {
            Card.OnDevicesChanged();
        }
    }
}

/// <summary>
/// The USB rules of one profile on its trigger tab (R-OBJ-1): the rules of <c>automation.json</c> filtered by the profile,
/// edited with the profile and written back with the other rules untouched when the profile is saved.
/// </summary>
public sealed partial class ProfileRulesEditor : ObservableObject, IRuleOwner
{
    private readonly Guid _profileId;
    private readonly IReadOnlyList<AutomationRule> _others;
    private readonly IReadOnlyList<AutomationRule> _initial;
    private readonly IReadOnlyList<Profile> _profiles;
    private readonly Guid? _defaultProfileId;
    private readonly IReadOnlyList<UsbDevice> _connected;
    private readonly IReadOnlyDictionary<string, string>? _customNames;
    private readonly IUsbPowerCheck _powerCheck;
    private readonly ILogger _log;
    private readonly Dictionary<string, string> _windowsNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _powerWarnings = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public ProfileRulesEditor(
        Guid profileId,
        IReadOnlyList<AutomationRule> allRules,
        IReadOnlyList<Profile> profiles,
        Guid? defaultProfileId,
        IReadOnlyList<UsbDevice> connected,
        IReadOnlyDictionary<string, string>? customNames,
        IUsbPowerCheck powerCheck,
        ILogger log)
    {
        ArgumentNullException.ThrowIfNull(allRules);
        ArgumentNullException.ThrowIfNull(log);
        _profileId = profileId;
        _others = allRules.Where(r => r.ProfileId != profileId).ToList();
        _initial = allRules.Where(r => r.ProfileId == profileId).ToList();
        _profiles = profiles;
        _defaultProfileId = defaultProfileId;
        _connected = connected;
        _customNames = customNames;
        _powerCheck = powerCheck;
        _log = log.ForContext<ProfileRulesEditor>();
        Rebuild(_initial);
    }

    /// <summary>Raised on every change a card makes.</summary>
    public event EventHandler? Changed;

    public ObservableCollection<RuleCard> Rules { get; } = [];

    public ObservableCollection<Choice> DeviceChoices { get; } = [];

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    public bool IsEmpty => Rules.Count == 0;

    /// <summary>Whether the rules differ from those on disk.</summary>
    public bool IsDirty => !SameRules(Build(), _initial);

    public IReadOnlyList<AutomationRule> Build() => Rules.Select(r => r.ToRule()).ToList();

    /// <summary>All rules for the settings: the other profiles' rules as they were, then this profile's.</summary>
    public IReadOnlyList<AutomationRule> Merge() => [.. _others, .. Build()];

    /// <summary>New texts after a language change, same rules.</summary>
    public void Relabel() => Rebuild(Build());

    public Choice? DeviceChoiceFor(string? deviceId) =>
        DeviceChoices.FirstOrDefault(c => string.Equals(c.Key, deviceId, StringComparison.OrdinalIgnoreCase));

    public string? DeviceNameFor(string? deviceId) =>
        deviceId is not null && _windowsNames.TryGetValue(deviceId, out string? name) ? UsbDeviceNames.NameOf(deviceId, name, _customNames) : null;

    public string? WindowsNameFor(string? deviceId) =>
        deviceId is not null && _windowsNames.TryGetValue(deviceId, out string? name) ? name : null;

    public string? DescribeDevices(IReadOnlyList<string> deviceIds)
    {
        ArgumentNullException.ThrowIfNull(deviceIds);
        return deviceIds.Count == 0 ? null : string.Join(" + ", deviceIds.Select(id => DeviceNameFor(id) ?? id));
    }

    public bool IsDeviceConnected(string? deviceId) =>
        deviceId is not null && _connected.Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    public Choice? ProfileChoiceFor(Guid id) =>
        _profiles.FirstOrDefault(p => p.Id == id) is { } profile ? new Choice(profile.Id.ToString("D"), profile.Name) : null;

    public bool HasPowerWarning(string? deviceId)
    {
        if (deviceId is null)
        {
            return false;
        }

        if (!_powerWarnings.TryGetValue(deviceId, out bool warn))
        {
            try
            {
                warn = UsbPowerSaving.ShouldWarn(_powerCheck.Check(deviceId));
            }
            catch (Exception ex) when (ex is Win32Exception or System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                _log.Debug(ex, "USB power check for {Device} failed", deviceId);
                warn = false;
            }

            _powerWarnings[deviceId] = warn;
        }

        return warn;
    }

    public void UpdateDuplicates()
    {
        // Duplicates across all profiles: the other profiles' rules count too.
        HashSet<string> shared = _others
            .Select(r => DeviceKeyOf(r.Devices))
            .OfType<string>()
            .Concat(Rules.Select(r => r.DeviceKey).OfType<string>())
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (RuleCard card in Rules)
        {
            card.HasDuplicateDevice = card.DeviceKey is { } key && shared.Contains(key);
        }
    }

    public void OnRuleDevicesChanged()
    {
    }

    public void OnCardChanged()
    {
        if (!_loading)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    [RelayCommand]
    private void AddRule()
    {
        (_, Guid exitProfile) = RuleExits.NewRuleProfiles(_profiles, _defaultProfileId);
        var rule = new AutomationRule
        {
            Devices = [],
            ProfileId = _profileId,
            OnExit = exitProfile == _profileId ? ExitAction.SwitchBack : ExitAction.SwitchTo,
            ExitProfileId = exitProfile == _profileId ? null : exitProfile,
        };
        Rules.Add(new RuleCard(this, rule) { IsExpanded = true });
        OnPropertyChanged(nameof(IsEmpty));
        UpdateDuplicates();
        _log.Information("USB rule {Rule} added to profile {Profile}", rule.Id, _profileId);
        OnCardChanged();
    }

    [RelayCommand]
    private void RemoveRule(RuleCard? card)
    {
        if (card is not null && Rules.Remove(card))
        {
            OnPropertyChanged(nameof(IsEmpty));
            UpdateDuplicates();
            _log.Information("USB rule {Rule} removed from profile {Profile}", card.Id, _profileId);
            OnCardChanged();
        }
    }

    private static string? DeviceKeyOf(IReadOnlyList<RuleDevice>? devices)
    {
        var ids = (devices ?? []).Select(d => UsbDeviceIds.Normalize(d.Id)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        return ids.Count == 0 ? null : string.Join('+', ids);
    }

    private static bool SameRules(IReadOnlyList<AutomationRule> a, IReadOnlyList<AutomationRule> b) =>
        a.Count == b.Count && a.Zip(b).All(pair => SameRule(pair.First, pair.Second));

    private static bool SameRule(AutomationRule a, AutomationRule b) =>
        a.Id == b.Id
        && a.ProfileId == b.ProfileId
        && a.OnExit == b.OnExit
        && a.ExitProfileId == b.ExitProfileId
        && a.SkipConfirmation == b.SkipConfirmation
        && a.ExitDelaySeconds == b.ExitDelaySeconds
        && DeviceKeyOf(a.Devices) == DeviceKeyOf(b.Devices);

    private void Rebuild(IReadOnlyList<AutomationRule> rules)
    {
        _loading = true;
        try
        {
            UsbDeviceChoices.Fill(DeviceChoices, _windowsNames, _connected, UsbDeviceChoices.Known([.. _others, .. rules], _profiles, _customNames), _customNames);
            IEnumerable<Guid> missing = rules.Select(r => r.ExitProfileId ?? r.ProfileId).Distinct().Where(id => _profiles.All(p => p.Id != id));
            RuleExits.Fill(ExitChoices, _profiles, missing);
            Rules.Clear();
            foreach (AutomationRule rule in rules)
            {
                Rules.Add(new RuleCard(this, rule));
            }

            UpdateDuplicates();
            foreach (RuleCard card in Rules)
            {
                card.UpdatePowerWarning();
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(IsEmpty));
    }
}
