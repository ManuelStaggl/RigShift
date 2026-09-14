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
/// Rules: "when this USB device connects, switch to that profile" (docs/PLAN.md, section 6). Changes save at once.
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
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _powerWarnings = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

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
    }

    /// <summary>Asks before a rule is deleted (analysis finding I-12); replaceable so tests run without a window.</summary>
    internal Func<string?, Task<bool>> ConfirmDeleteRule { get; set; } = ProfileDialogs.ConfirmDeleteRuleAsync;

    public ObservableCollection<RuleCard> Rules { get; } = [];

    /// <summary>Connected USB devices, plus devices of rules that are not connected right now.</summary>
    public ObservableCollection<Choice> DeviceChoices { get; } = [];

    public ObservableCollection<Choice> ProfileChoices { get; } = [];

    public ObservableCollection<Choice> ExitChoices { get; } = [];

    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddRuleCommand))]
    public partial bool HasNoProfiles { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    public void Load() => Rebuild(_automation.Rules);

    private void Rebuild(IReadOnlyList<AutomationRule> rules) => Quietly(() =>
    {
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
        RefreshPowerWarnings();
        UpdateDuplicates();
    });

    /// <summary>Marks cards whose device another rule watches too: both switch when it connects (analysis finding C-05).</summary>
    internal void UpdateDuplicates()
    {
        HashSet<string> shared = Rules
            .Select(r => r.DeviceId)
            .OfType<string>()
            .GroupBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (RuleCard card in Rules)
        {
            card.HasDuplicateDevice = card.DeviceId is { } id && shared.Contains(id);
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

    /// <summary>The device's own name, without the "not connected" note.</summary>
    internal string? DeviceNameFor(string? deviceId) => deviceId is not null && _deviceNames.TryGetValue(deviceId, out string? name) ? name : null;

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
            UsbDeviceId = device ?? string.Empty,
            UsbDeviceName = DeviceNameFor(device),
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
        if (card is null || !await ConfirmDeleteRule(DeviceNameFor(card.DeviceId)))
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
        List<AutomationRule> rules = Rules.Select(r => r.ToRule()).ToList();
        FillDevices(rules);
        foreach (RuleCard card in Rules)
        {
            card.SelectedDevice = DeviceChoiceFor(card.DeviceId);
        }

        RefreshPowerWarnings();
    });

    private void FillDevices(IReadOnlyList<AutomationRule> rules)
    {
        IReadOnlyList<UsbDevice> connected;
        try
        {
            connected = _devices.ConnectedDevices();
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed");
            connected = [];
        }

        DeviceChoices.Clear();
        _deviceNames.Clear();
        foreach (UsbDevice device in connected)
        {
            DeviceChoices.Add(new Choice(device.Id, device.Name));
            _deviceNames[device.Id] = device.Name;
        }

        foreach (AutomationRule rule in rules)
        {
            if (UsbDeviceIds.Normalize(rule.UsbDeviceId) is { } id && !_deviceNames.ContainsKey(id))
            {
                string name = rule.UsbDeviceName ?? id;
                DeviceChoices.Add(new Choice(id, Loc.Format("Automation_DeviceNotConnected", name)));
                _deviceNames[id] = name;
            }
        }

        _log.Debug("Automation lists {Count} USB device(s)", connected.Count);
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
        SelectedDevice = owner.DeviceChoiceFor(UsbDeviceIds.Normalize(rule.UsbDeviceId));
        SelectedProfile = owner.ProfileChoiceFor(rule.ProfileId);
        SelectedExit = owner.ExitChoiceFor(rule);
        SkipConfirmation = rule.SkipConfirmation;
        ExitDelaySeconds = rule.ExitDelaySeconds;
    }

    public Guid Id { get; }

    public AutomationViewModel Owner => _owner;

    internal string? DeviceId => SelectedDevice?.Key;

    [ObservableProperty]
    public partial Choice? SelectedDevice { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedProfile { get; set; }

    [ObservableProperty]
    public partial Choice? SelectedExit { get; set; }

    [ObservableProperty]
    public partial bool SkipConfirmation { get; set; }

    [ObservableProperty]
    public partial double? ExitDelaySeconds { get; set; }

    /// <summary>Windows may power the chosen device down (hint only, docs/usb-power-saving.md).</summary>
    [ObservableProperty]
    public partial bool HasPowerWarning { get; private set; }

    internal void UpdatePowerWarning() => HasPowerWarning = _owner.HasPowerWarning(DeviceId);

    /// <summary>Another rule watches the same device (analysis finding C-05).</summary>
    [ObservableProperty]
    public partial bool HasDuplicateDevice { get; internal set; }

    public AutomationRule ToRule()
    {
        (ExitAction onExit, Guid? exitProfile) = AutomationViewModel.ExitFrom(SelectedExit);
        return new AutomationRule
        {
            Id = Id,
            // An empty id keeps a rule without a chosen device; it watches nothing until one is picked.
            UsbDeviceId = DeviceId ?? string.Empty,
            UsbDeviceName = _owner.DeviceNameFor(DeviceId),
            ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profile) ? profile : Guid.Empty,
            OnExit = onExit,
            ExitProfileId = exitProfile,
            SkipConfirmation = SkipConfirmation,
            ExitDelaySeconds = ExitDelaySeconds is { } seconds
                ? (int)Math.Clamp(Math.Round(seconds), 0, AutomationRule.MaxExitDelaySeconds)
                : AutomationRule.DefaultExitDelaySeconds,
        };
    }

    partial void OnSelectedDeviceChanged(Choice? value)
    {
        UpdatePowerWarning();
        _owner.UpdateDuplicates();
        _owner.OnCardChanged();
    }

    partial void OnSelectedProfileChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSelectedExitChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSkipConfirmationChanged(bool value) => _owner.OnCardChanged();

    partial void OnExitDelaySecondsChanged(double? value) => _owner.OnCardChanged();
}
