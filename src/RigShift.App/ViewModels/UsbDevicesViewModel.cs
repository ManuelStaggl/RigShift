using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.App.ViewModels;

/// <summary>
/// The settings' device group: the USB devices rules and profiles use, their custom names (user decision U-01) and
/// the switch that pauses every rule at once. The rules themselves live in the profile that owns them.
/// </summary>
public sealed partial class UsbDevicesViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly ProfileCatalog _catalog;
    private readonly AutomationService _automation;
    private readonly IUsbDeviceList _devices;
    private readonly ILogger _log;
    private bool _loading;

    public UsbDevicesViewModel(
        SettingsService settings, ProfileCatalog catalog, AutomationService automation, IUsbDeviceList devices, ILogger log)
    {
        ArgumentNullException.ThrowIfNull(automation);
        ArgumentNullException.ThrowIfNull(log);
        _settings = settings;
        _catalog = catalog;
        _automation = automation;
        _devices = devices;
        _log = log.ForContext<UsbDevicesViewModel>();

        // A rule saved in a profile editor brings a new device, and the tray menu can pause: both without reopening.
        automation.Changed += (_, _) => Refresh();

        // "Connected" and "not connected" are built in code, so they follow a language change (I-13).
        Loc.Instance.PropertyChanged += (_, _) => Refresh();
    }

    /// <summary>
    /// Devices that can be named: the ones rules and profiles use, and named ones. Not every connected device – a PC
    /// lists hubs, receivers and keyboards no rule ever needs.
    /// </summary>
    public ObservableCollection<UsbNameCard> NamedDevices { get; } = [];

    [ObservableProperty]
    public partial bool HasNoNamedDevices { get; set; } = true;

    /// <summary>Pauses every USB rule at once; the same switch sits in the tray menu.</summary>
    [ObservableProperty]
    public partial bool IsPaused { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    /// <summary>Rereads the connected devices and the paused state; the page does this whenever it opens.</summary>
    public void Refresh() => Quietly(() =>
    {
        IsPaused = _automation.IsPaused;

        Dictionary<string, string> windowsNames = ListConnected()
            .GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Name, StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string>? customNames = _settings.Current.UsbDeviceNames;

        // A device without Windows' name (only named) keeps the name a rule stored with it, so it is not shown as an id.
        var known = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (RuleDevice device in UsbDeviceChoices.Known(_automation.Rules, _catalog.Profiles, customNames))
        {
            if (UsbDeviceIds.Normalize(device.Id) is { } id && (!known.TryGetValue(id, out string? name) || name is null))
            {
                known[id] = windowsNames.GetValueOrDefault(id) ?? (string.IsNullOrWhiteSpace(device.Name) ? null : device.Name);
            }
        }

        // Connected devices first: those are the ones the user is holding while reading the table.
        NamedDevices.Clear();
        foreach ((string id, string? name) in known
            .OrderByDescending(p => windowsNames.ContainsKey(p.Key))
            .ThenBy(p => p.Value ?? p.Key, StringComparer.CurrentCultureIgnoreCase))
        {
            NamedDevices.Add(new UsbNameCard(
                this, id, name ?? id, windowsNames.ContainsKey(id), UsbDeviceNames.CustomNameOf(id, customNames)));
        }

        HasNoNamedDevices = NamedDevices.Count == 0;
    });

    /// <summary>Saves a custom name; it shows up in rules, profiles and messages at once.</summary>
    internal async Task RenameAsync(UsbNameCard card, string? name)
    {
        ArgumentNullException.ThrowIfNull(card);
        try
        {
            await _settings.UpdateAsync(
                s => s with { UsbDeviceNames = UsbDeviceNames.WithName(s.UsbDeviceNames, card.Id, name) }, CancellationToken.None);
            ErrorMessage = null;
            _log.Information("USB device {Device} named {Name}", card.Id, name ?? "(none)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "USB device {Device} could not be renamed", card.Id);
            ErrorMessage = Loc.Format("Status_Error", ex.Message);
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

    private IReadOnlyList<UsbDevice> ListConnected()
    {
        try
        {
            return _devices.ConnectedDevices();
        }
        catch (Win32Exception ex)
        {
            _log.Warning(ex, "USB devices could not be listed");
            return [];
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
