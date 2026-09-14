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
/// Rules: "when this game starts or this USB device connects, switch to that profile" (docs/PLAN.md, section 6).
/// Changes save at once.
/// </summary>
public sealed partial class AutomationViewModel : ObservableObject
{
    internal const string CustomGameKey = "custom";
    internal const string UsbDeviceKey = "usb";
    private const string ExitStayKey = "stay";
    private const string ExitBackKey = "back";
    private const string ExitToPrefix = "to:";

    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AutomationService _automation;
    private readonly IUsbDeviceList _devices;
    private readonly ILogger _log;
    private readonly Dictionary<string, string> _deviceNames = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public AutomationViewModel(SettingsService settings, ProfileCatalog catalog, AutomationService automation, IUsbDeviceList devices, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(log);

        _settings = settings;
        _catalog = catalog;
        _automation = automation;
        _devices = devices;
        _log = log.ForContext<AutomationViewModel>();
        automation.Changed += (_, _) => Quietly(() => IsPaused = automation.IsPaused);
    }

    public ObservableCollection<RuleCard> Rules { get; } = [];

    public ObservableCollection<Choice> GameChoices { get; } = [];

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

    public void Load() => Quietly(() =>
    {
        IReadOnlyList<AutomationRule> rules = _automation.Rules;

        GameChoices.Clear();
        foreach (GameTemplate template in GameTemplates.BuiltIn)
        {
            GameChoices.Add(new Choice(template.Id, template.Name));
        }

        GameChoices.Add(new Choice(CustomGameKey, Loc.Instance["Automation_CustomGame"]));
        GameChoices.Add(new Choice(UsbDeviceKey, Loc.Instance["Automation_UsbDevice"]));

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
    });

    internal Choice? GameChoiceFor(AutomationRule rule) =>
        GameChoices.FirstOrDefault(c => c.Key == (rule.UsbDeviceId is not null ? UsbDeviceKey : rule.TemplateId ?? CustomGameKey));

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
            _ = _automation.SetPausedAsync(value);
        }
    }

    [RelayCommand(CanExecute = nameof(CanAddRule))]
    private async Task AddRuleAsync()
    {
        var rule = new AutomationRule
        {
            TemplateId = GameTemplates.BuiltIn.Count > 0 ? GameTemplates.BuiltIn[0].Id : null,
            ProfileId = _catalog.Profiles.FirstOrDefault(p => p.Id == _settings.Current.DefaultProfileId)?.Id ?? _catalog.Profiles[0].Id,
            OnExit = ExitAction.SwitchBack,
        };
        Quietly(() => Rules.Add(new RuleCard(this, rule)));
        IsEmpty = false;
        _log.Information("Automation rule {Rule} added", rule.Id);
        await SaveAsync();
    }

    private bool CanAddRule() => !HasNoProfiles;

    [RelayCommand]
    private async Task DeleteRuleAsync(RuleCard? card)
    {
        if (card is null)
        {
            return;
        }

        Rules.Remove(card);
        IsEmpty = Rules.Count == 0;
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
        IsEnabled = rule.IsEnabled;
        SelectedGame = owner.GameChoiceFor(rule);
        ExecutablePath = rule.ExecutablePath ?? string.Empty;
        SelectedDevice = owner.DeviceChoiceFor(UsbDeviceIds.Normalize(rule.UsbDeviceId));
        SelectedProfile = owner.ProfileChoiceFor(rule.ProfileId);
        SelectedExit = owner.ExitChoiceFor(rule);
        SkipConfirmation = rule.SkipConfirmation;
        ExitDelaySeconds = rule.ExitDelaySeconds;
    }

    public Guid Id { get; }

    public AutomationViewModel Owner => _owner;

    public bool IsCustom => SelectedGame?.Key == AutomationViewModel.CustomGameKey;

    public bool IsUsb => SelectedGame?.Key == AutomationViewModel.UsbDeviceKey;

    public string TriggerLabel => Loc.Instance[IsUsb ? "Automation_DeviceConnects" : "Automation_Game"];

    public string EndLabel => Loc.Instance[IsUsb ? "Automation_DeviceGone" : "Automation_OnExit"];

    internal string? DeviceId => SelectedDevice?.Key;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustom), nameof(IsUsb), nameof(TriggerLabel), nameof(EndLabel))]
    public partial Choice? SelectedGame { get; set; }

    [ObservableProperty]
    public partial string ExecutablePath { get; set; }

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

    public AutomationRule ToRule()
    {
        (ExitAction onExit, Guid? exitProfile) = AutomationViewModel.ExitFrom(SelectedExit);
        return new AutomationRule
        {
            Id = Id,
            IsEnabled = IsEnabled,
            TemplateId = IsCustom || IsUsb ? null : SelectedGame?.Key,
            ExecutablePath = IsCustom && !string.IsNullOrWhiteSpace(ExecutablePath) ? ExecutablePath.Trim() : null,

            // An empty id keeps a USB rule without a chosen device a USB rule; it watches nothing until one is picked.
            UsbDeviceId = IsUsb ? DeviceId ?? string.Empty : null,
            UsbDeviceName = IsUsb ? _owner.DeviceNameFor(DeviceId) : null,
            ProfileId = Guid.TryParse(SelectedProfile?.Key, out Guid profile) ? profile : Guid.Empty,
            OnExit = onExit,
            ExitProfileId = exitProfile,
            SkipConfirmation = SkipConfirmation,
            ExitDelaySeconds = ExitDelaySeconds is { } seconds
                ? (int)Math.Clamp(Math.Round(seconds), 0, AutomationRule.MaxExitDelaySeconds)
                : AutomationRule.DefaultExitDelaySeconds,
        };
    }

    partial void OnIsEnabledChanged(bool value) => _owner.OnCardChanged();

    partial void OnSelectedGameChanged(Choice? value)
    {
        // Picking "USB device" preselects the first connected device, so the rule works without a second click.
        if (IsUsb && SelectedDevice is null && _owner.DeviceChoices.Count > 0)
        {
            SelectedDevice = _owner.DeviceChoices[0];
        }

        _owner.OnCardChanged();
    }

    partial void OnExecutablePathChanged(string value) => _owner.OnCardChanged();

    partial void OnSelectedDeviceChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSelectedProfileChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSelectedExitChanged(Choice? value) => _owner.OnCardChanged();

    partial void OnSkipConfirmationChanged(bool value) => _owner.OnCardChanged();

    partial void OnExitDelaySecondsChanged(double? value) => _owner.OnCardChanged();
}
