using System.Collections.ObjectModel;
using RigShift.App.Localization;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;

namespace RigShift.App.ViewModels;

/// <summary>
/// The USB device list of the automation page and the profile editor (analysis finding A-08): connected devices first, then
/// saved devices that are not connected, each shown with its custom name (user decision U-01).
/// </summary>
internal static class UsbDeviceChoices
{
    /// <summary>
    /// Every device RigShift knows without it being connected: the devices of the rules, the devices profiles wait for and
    /// named devices. A device picked once stays selectable while it is off (finding HW-08).
    /// </summary>
    public static IEnumerable<RuleDevice> Known(
        IEnumerable<AutomationRule>? rules, IEnumerable<Profile> profiles, IReadOnlyDictionary<string, string>? customNames)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        return (rules ?? [])
            .SelectMany(r => r.Devices ?? [])
            .Concat(profiles.Select(p => new RuleDevice { Id = p.AppsWaitForUsbDeviceId, Name = p.AppsWaitForUsbDeviceName }))
            .Concat((customNames?.Keys ?? []).Select(id => new RuleDevice { Id = id }));
    }

    /// <param name="windowsNames">Filled with Windows' name per device id, which rules and profiles store with the id.</param>
    public static void Fill(
        ObservableCollection<Choice> choices,
        IDictionary<string, string> windowsNames,
        IReadOnlyList<UsbDevice> connected,
        IEnumerable<RuleDevice> saved,
        IReadOnlyDictionary<string, string>? customNames)
    {
        ArgumentNullException.ThrowIfNull(choices);
        ArgumentNullException.ThrowIfNull(windowsNames);
        ArgumentNullException.ThrowIfNull(connected);
        ArgumentNullException.ThrowIfNull(saved);

        choices.Clear();
        windowsNames.Clear();
        foreach (UsbDevice device in connected)
        {
            if (windowsNames.TryAdd(device.Id, device.Name))
            {
                choices.Add(new Choice(device.Id, UsbDeviceNames.Label(device.Id, device.Name, customNames)));
            }
        }

        // A device without Windows' name (only named) gets one from a later entry that has it, so its label is not the id.
        var savedNames = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (RuleDevice device in saved)
        {
            if (UsbDeviceIds.Normalize(device.Id) is { } id && !windowsNames.ContainsKey(id)
                && (!savedNames.TryGetValue(id, out string? name) || name is null))
            {
                savedNames[id] = string.IsNullOrWhiteSpace(device.Name) ? null : device.Name;
            }
        }

        foreach ((string id, string? savedName) in savedNames)
        {
            string name = savedName ?? id;
            windowsNames[id] = name;
            choices.Add(new Choice(id, Loc.Format("Automation_DeviceNotConnected", UsbDeviceNames.Label(id, name, customNames))));
        }
    }
}
