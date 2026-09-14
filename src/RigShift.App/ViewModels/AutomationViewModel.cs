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
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// Rules: "when these USB devices are connected, switch to that profile" (docs/PLAN.md, section 6), and the custom USB
/// device names (user decision U-01). Changes save at once.
/// </summary>
public sealed partial class AutomationViewModel : ObservableObject
{
    private const string ExitStayKey = "stay";
    private const string ExitBackKey = "back";
    private const string ExitToPrefix = "to:";

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AutomationService _automation;
    private const string UsbPowerHelpUrl = "https://github.com/ManuelStaggl/RigShift/blob/main/docs/usb-power-saving.md";

    private readonly IUsbDeviceList _devices;
    private readonly IUsbPowerCheck _powerCheck;
    private readonly ILogger _log;

    /// <summary>Windows' name per device id: connected devices and saved devices that are not connected.</summary>
    private readonly Dictionary<string, string> _windowsNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _powerWarnings = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<UsbDevice> _connected = [];
    private bool _loading;
    private bool _loaded;

    public AutomationViewModel(
        SettingsService settings, ProfileCatalog catalog, AutomationService automation, IUsbDeviceList devices, IUsbPowerCheck powerCheck, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _automation = automation;
        _devices = devices;
        _powerCheck = powerCheck;
        _log = log.ForContext<AutomationViewModel>();
        automation.Changed += (_, _) => Quietly(() => IsPaused = automation.IsPaused);

        // Choice names ("Stay in the profile", "(not connected)") are built in code; rebuild them from the cards as they
        // stand on a language change (I-13). Rebuilding runs quietly, so it saves nothing.
        Loc.Instance.PropertyChanged += (_, _) =>
        {
            if (_loaded)
            {
                Rebuild(Rules.Select(r => r.ToRule()).ToList());
            }
        };
    }

    /// <summary>Asks before a rule is deleted (analysis finding I-12); replaceable so tests run without a window.</summary>
    internal Func<string?, Task<bool>> ConfirmDeleteRule { get; set; } = ProfileDialogs.ConfirmDeleteRuleAsync;

    public ObservableCollection<RuleCard> Rules { get; } = [];

    /// <summary>Connected USB devices, plus devices of rules that are not connected right now.</summary>
    public ObservableCollection<Choice> DeviceChoices { get; } = [];

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    /// <summary>Devices that can be named: connected ones, the ones rules and profiles use, and named ones.</summary>
    public ObservableCollection<UsbNameCard> NamedDevices { get; } = [];

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial bool HasNoProfiles { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool HasNoNamedDevices { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    private IReadOnlyDictionary<string, string>? CustomNames => _settings.Current.UsbDeviceNames;

    public void Load() => Rebuild(_automation.Rules);

    private void Rebuild(IReadOnlyList<AutomationRule> rules) => Quietly(() =>
    {
        _loaded = true;
        _connected = ListConnected();
        FillDevices(rules);

        ProfileChoices.Clear();
        ExitChoices.Clear();
        ExitChoices.Add(new Choice(ExitStayKey, Loc.Instance["Automation_ExitStay"]));
        ExitChoices.Add(new Choice(ExitBackKey, Loc.Instance["Automation_ExitBack"]));
        foreach (Profile profile in _catalog.Profiles)
        {
            ProfileChoices.Add(new Choice(profile.Id.ToString("D"), profile.Name));
            ExitChoices.Add(new Choice(ExitToPrefix + profile.Id.ToString("D"), Loc.Format("Automation_ExitTo", profile.Name)));
        }

        // A rule may point at a deleted profile; keep it visible instead of silently changing the rule.
        foreach (Guid missing in rules.SelectMany(r => new[] { r.ProfileId, r.ExitProfileId ?? r.ProfileId }).Distinct().Where(id => _catalog.Find(id) is null))
        {
            ProfileChoices.Add(new Choice(missing.ToString("D"), Loc.Instance["Automation_MissingProfile"]));
            ExitChoices.Add(new Choice(ExitToPrefix + missing.ToString("D"), Loc.Format("Automation_ExitTo", Loc.Instance["Automation_MissingProfile"])));
        }

        Rules.Clear();
        foreach (AutomationRule rule in rules)
        {
            Rules.Add(new RuleCard(this, rule));
        }

        IsPaused = _automation.IsPaused;
        HasNoProfiles = _catalog.Profiles.Count == 0;
        IsEmpty = Rules.Count == 0;
        FillNamedDevices(rules);
        RefreshPowerWarnings();
        UpdateDuplicates();
    });

    /// <summary>
    /// Marks cards whose devices another rule watches too: both switch when they connect (analysis finding C-05). Only the
    /// same set counts; a rule for the wheel and one for the wheel with a headset are a deliberate pair.
    /// </summary>
    internal void UpdateDuplicates()
    {
        HashSet<string> shared = Rules
            .Select(r => r.DeviceKey)
            .OfType<string>()
            .GroupBy(key => key, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (RuleCard card in Rules)
        {
            card.HasDuplicateDevice = card.DeviceKey is { } key && shared.Contains(key);
        }
    }

    /// <summary>Whether Windows may power the device down; checked once per device until the next refresh.</summary>
    internal bool HasPowerWarning(string? deviceId)
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
            if (warn)
            {
                _log.Information("USB power saving may turn off {Device}", DeviceNameFor(deviceId) ?? deviceId);
            }
        }

        return warn;
    }

    private void RefreshPowerWarnings()
    {
        _powerWarnings.Clear();
        foreach (RuleCard card in Rules)
        {
            card.UpdatePowerWarning();
        }
    }

    [RelayCommand]
    private void OpenUsbPowerHelp() => ShellFolders.OpenUrl(UsbPowerHelpUrl, _log);

    internal Choice? DeviceChoiceFor(string? deviceId) =>
        DeviceChoices.FirstOrDefault(c => string.Equals(c.Key, deviceId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The device's name for messages – its custom name, else Windows' name – without the "not connected" note.</summary>
    internal string? DeviceNameFor(string? deviceId) =>
        deviceId is not null && _windowsNames.TryGetValue(deviceId, out string? name) ? UsbDeviceNames.NameOf(deviceId, name, CustomNames) : null;

    /// <summary>Windows' name, stored with the rule so a device that is not connected still has one.</summary>
    internal string? WindowsNameFor(string? deviceId) =>
        deviceId is not null && _windowsNames.TryGetValue(deviceId, out string? name) ? name : null;

    /// <summary>"Wheel + Pedals"; <c>null</c> without devices.</summary>
    internal string? DescribeDevices(IReadOnlyList<string> deviceIds) =>
        deviceIds.Count == 0 ? null : string.Join(" + ", deviceIds.Select(id => DeviceNameFor(id) ?? id));

    internal Choice? ProfileChoiceFor(Guid id) => ProfileChoices.FirstOrDefault(c => c.Key == id.ToString("D"));

    internal Choice? ExitChoiceFor(AutomationRule rule) => rule.OnExit switch
    {
        ExitAction.SwitchBack => ExitChoices.FirstOrDefault(c => c.Key == ExitBackKey),
        ExitAction.SwitchTo when rule.ExitProfileId is { } id => ExitChoices.FirstOrDefault(c => c.Key == ExitToPrefix + id.ToString("D")),
        _ => ExitChoices.FirstOrDefault(c => c.Key == ExitStayKey),
    };

    internal static (ExitAction Action, Guid? Profile) ExitFrom(Choice? choice) => choice?.Key switch
    {
        ExitBackKey => (ExitAction.SwitchBack, null),
        { } key when key.StartsWith(ExitToPrefix, StringComparison.Ordinal) && Guid.TryParse(key[ExitToPrefix.Length..], out Guid id) => (ExitAction.SwitchTo, id),
        _ => (ExitAction.Stay, null),
    };

    internal void OnCardChanged()
    {
        if (!_loading)
        {
            _ = SaveAsync();
        }
    }

    partial void OnIsPausedChanged(bool value)
    {
        if (!_loading)
        {
            _ = SetPausedAsync(value);
        }
    }

    /// <summary>A failed save shows an error and puts the switch back to the state on disk (analysis finding A-07).</summary>
    private async Task SetPausedAsync(bool paused)
    {
        if (await _automation.SetPausedAsync(paused))
        {
            ErrorMessage = null;
            return;
        }

        Quietly(() => IsPaused = _automation.IsPaused);
        ErrorMessage = Loc.Instance["Automation_PauseFailed"];
    }

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private async Task AddRuleAsync()
    {
        // The first connected device is preselected, so the rule works without a second click.
        string? device = DeviceChoices.Count > 0 ? DeviceChoices[0].Key : null;
        (Guid profile, Guid exitProfile) = NewRuleProfiles(_catalog.Profiles, _settings.Current.DefaultProfileId);
        var rule = new AutomationRule
        {
            Devices = device is null ? [] : [new RuleDevice { Id = device, Name = WindowsNameFor(device) }],
            ProfileId = profile,
            OnExit = ExitAction.SwitchTo,
            ExitProfileId = exitProfile,
        };
        Quietly(() => Rules.Add(new RuleCard(this, rule)));
        IsEmpty = false;
        UpdateDuplicates();
        _log.Information("Automation rule {Rule} added", rule.Id);
        await SaveAsync();
    }

    private bool CanAddRule() => !HasNoProfiles;

    /// <summary>
    /// Profiles a new rule starts with: its end action switches to the default profile (the desk), or to the first profile
    /// without a default – deterministic, unlike "switch back" (user decision O-08). The rule itself switches to the first
    /// other profile.
    /// </summary>
    internal static (Guid Profile, Guid ExitProfile) NewRuleProfiles(IReadOnlyList<Profile> profiles, Guid? defaultProfileId)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentOutOfRangeException.ThrowIfZero(profiles.Count);

        Profile exit = profiles.FirstOrDefault(p => p.Id == defaultProfileId) ?? profiles[0];
        Profile start = profiles.FirstOrDefault(p => p.Id != exit.Id) ?? exit;
        return (start.Id, exit.Id);
    }

    [RelayCommand]
    private async Task DeleteRuleAsync(RuleCard? card)
    {
        if (card is null || !await ConfirmDeleteRule(DescribeDevices(card.DeviceIds)))
        {
            return;
        }

        Rules.Remove(card);
        IsEmpty = Rules.Count == 0;
        UpdateDuplicates();
        _log.Information("Automation rule {Rule} deleted", card.Id);
        await SaveAsync();
    }

    [RelayCommand]
    private void RefreshDevices() => Quietly(() =>
    {
        _connected = ListConnected();
        Relabel();
        FillNamedDevices(Rules.Select(r => r.ToRule()).ToList());
        RefreshPowerWarnings();
    });

    /// <summary>Saves a custom name and shows it in every device list at once.</summary>
    internal async Task RenameDeviceAsync(UsbNameCard card, string? name)
    {
        try
        {
            await _settings.UpdateAsync(s => s with { UsbDeviceNames = UsbDeviceNames.WithName(s.UsbDeviceNames, card.Id, name) }, CancellationToken.None);
            ErrorMessage = null;
            _log.Information("USB device {Device} named {Name}", card.Id, name ?? "(none)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "USB device {Device} could not be renamed", card.Id);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
            return;
        }

        Quietly(Relabel);
    }

    /// <summary>Refills the device list from <see cref="_connected"/> and puts each rule's devices back.</summary>
    private void Relabel()
    {
        // Rules are read before the list is refilled: clearing it makes each ComboBox write null into its device.
        List<AutomationRule> rules = Rules.Select(r => r.ToRule()).ToList();
        FillDevices(rules);
        for (int i = 0; i < Rules.Count; i++)
        {
            Rules[i].SetDevices(rules[i]);
        }
    }

    private IReadOnlyList<UsbDevice> ListConnected()
    {
        try
        {
            IReadOnlyList<UsbDevice> connected = _devices.ConnectedDevices();
            _log.Debug("Automation lists {Count} USB device(s)", connected.Count);
            return connected;
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed");
            return [];
        }
    }

    private void FillDevices(IReadOnlyList<AutomationRule> rules) =>
        UsbDeviceChoices.Fill(DeviceChoices, _windowsNames, _connected, rules.SelectMany(r => r.Devices ?? []), CustomNames);

    private void FillNamedDevices(IReadOnlyList<AutomationRule> rules)
    {
        var known = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (UsbDevice device in _connected)
        {
            known.TryAdd(device.Id, device.Name);
        }

        IEnumerable<RuleDevice> saved = rules
            .SelectMany(r => r.Devices ?? [])
            .Concat(_catalog.Profiles.Select(p => new RuleDevice { Id = p.AppsWaitForUsbDeviceId, Name = p.AppsWaitForUsbDeviceName }))
            .Concat((CustomNames ?? new Dictionary<string, string>()).Keys.Select(id => new RuleDevice { Id = id }));
        foreach (RuleDevice device in saved)
        {
            if (UsbDeviceIds.Normalize(device.Id) is { } id && (!known.TryGetValue(id, out string? name) || name is null))
            {
                known[id] = string.IsNullOrWhiteSpace(device.Name) ? null : device.Name;
            }
        }

        HashSet<string> connected = _connected.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        NamedDevices.Clear();
        foreach ((string id, string? name) in known.OrderByDescending(p => connected.Contains(p.Key)).ThenBy(p => p.Value ?? p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            NamedDevices.Add(new UsbNameCard(this, id, name ?? id, connected.Contains(id), UsbDeviceNames.CustomNameOf(id, CustomNames)));
        }

        HasNoNamedDevices = NamedDevices.Count == 0;
    }

    private async Task SaveAsync()
    {
        List<AutomationRule> rules = Rules.Select(r => r.ToRule()).ToList();
        try
        {
            await _settings.UpdateAsync(s => s with { AutomationRules = rules }, CancellationToken.None);
            ErrorMessage = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Automation rules could not be saved");
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
        }
    }

    private void Quietly(Action action)
    {
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            action();
        }
        finally
        {
            _loading = wasLoading;
        }
    }
}

/// <summary>One rule on the automation page.</summary>
public sealed partial class RuleCard : ObservableObject
{
    private readonly AutomationViewModel _owner;

    public RuleCard(AutomationViewModel owner, AutomationRule rule)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(rule);

        _owner = owner;
        Id = rule.Id;
        SetDevices(rule);
        SelectedProfile = owner.ProfileChoiceFor(rule.ProfileId);
        SelectedExit = owner.ExitChoiceFor(rule);
        SkipConfirmation = rule.SkipConfirmation;
        ExitDelaySeconds = rule.ExitDelaySeconds;
    }

    public Guid Id { get; }

    public AutomationViewModel Owner => _owner;

    /// <summary>One entry per device; a combination switches once all of them are connected (user decision U-02).</summary>
    public ObservableCollection<RuleDeviceSlot> Devices { get; } = [];

    public bool IsCombination => Devices.Count > 1;

    public string DevicesHeader => Loc.Instance[IsCombination ? "Automation_DevicesConnect" : "Automation_DeviceConnects"];

    /// <summary>"Wheel + Pedals", for screen readers and the delete question.</summary>
    public string? DevicesText => _owner.DescribeDevices(DeviceIds);

    internal IReadOnlyList<string> DeviceIds => Devices
        .Select(s => s.SelectedDevice?.Key)
        .OfType<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>The device set, independent of order; <c>null</c> without devices.</summary>
    internal string? DeviceKey => DeviceIds.Count == 0 ? null : string.Join('+', DeviceIds.Order(StringComparer.OrdinalIgnoreCase));

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
    public partial bool HasPowerWarning { get; private set; }

    internal void UpdatePowerWarning() => HasPowerWarning = DeviceIds.Any(_owner.HasPowerWarning);

    /// <summary>Another rule watches the same devices (analysis finding C-05).</summary>
    [ObservableProperty]
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

    [RelayCommand]
    private void AddDevice()
    {
        IReadOnlyList<string> taken = DeviceIds;
        Choice? next = _owner.DeviceChoices.FirstOrDefault(c => c.Key is { } key && !taken.Contains(key, StringComparer.OrdinalIgnoreCase));
        Devices.Add(new RuleDeviceSlot(this, next));
        OnDevicesShapeChanged();
        OnDevicesChanged();
    }

    [RelayCommand]
    private void RemoveDevice(RuleDeviceSlot? slot)
    {
        if (slot is null || Devices.Count <= 1 || !Devices.Remove(slot))
        {
            return;
        }

        OnDevicesShapeChanged();
        OnDevicesChanged();
    }

    public AutomationRule ToRule()
    {
        (ExitAction onExit, Guid? exitProfile) = AutomationViewModel.ExitFrom(SelectedExit);
        return new AutomationRule
        {
            Id = Id,
            // An empty list keeps a rule without a chosen device; it watches nothing until one is picked.
            Devices = DeviceIds.Select(id => new RuleDevice { Id = id, Name = _owner.WindowsNameFor(id) }).ToList(),
            ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profile) ? profile : Guid.Empty,
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
        _owner.UpdateDuplicates();
        _owner.OnCardChanged();
    }

    private void OnDevicesShapeChanged()
    {
        OnPropertyChanged(nameof(IsCombination));
        OnPropertyChanged(nameof(DevicesHeader));
        OnPropertyChanged(nameof(DevicesText));
    }

    partial void OnSelectedProfileChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSelectedExitChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSkipConfirmationChanged(bool value) => _owner.OnCardChanged();

    partial void OnExitDelaySecondsChanged(double? value) => _owner.OnCardChanged();
}

/// <summary>One device of a rule.</summary>
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
    public partial Choice? SelectedDevice { get; set; }

    partial void OnSelectedDeviceChanged(Choice? value)
    {
        if (_ready)
        {
            Card.OnDevicesChanged();
        }
    }
}

/// <summary>A USB device and its custom name. The name is saved when the field loses focus or on Enter.</summary>
public sealed partial class UsbNameCard : ObservableObject
{
    private readonly AutomationViewModel _owner;
    private string? _savedName;

    public UsbNameCard(AutomationViewModel owner, string id, string windowsName, bool isConnected, string? customName)
    {
        _owner = owner;
        Id = id;
        WindowsName = windowsName;
        _savedName = UsbDeviceNames.Normalize(customName);
        CustomName = _savedName ?? string.Empty;
        DetailsText = Loc.Instance[isConnected ? "Automation_NameConnected" : "Automation_NameNotConnected"] + " · " + id;
    }

    public string Id { get; }

    public string WindowsName { get; }

    public string DetailsText { get; }

    [ObservableProperty]
    public partial string CustomName { get; set; }

    internal async Task SaveNameAsync()
    {
        string? name = UsbDeviceNames.Normalize(CustomName);
        if (name == _savedName)
        {
            return;
        }

        _savedName = name;
        await _owner.RenameDeviceAsync(this, name);
    }
}
