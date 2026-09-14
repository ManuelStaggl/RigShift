using System.Collections.ObjectModel;
using RigShift.App.Localization;
using RigShift.Core.Automation;

namespace RigShift.App.ViewModels;

/// <summary>
/// The USB device list of the automation page and the profile editor (analysis finding A-08): connected devices first, then
/// saved devices that are not connected, each shown with its custom name (user decision U-01).
/// </summary>
internal static class UsbDeviceChoices
{
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

        foreach (RuleDevice device in saved)
        {
            if (UsbDeviceIds.Normalize(device.Id) is { } id && !windowsNames.ContainsKey(id))
            {
                string name = string.IsNullOrWhiteSpace(device.Name) ? id : device.Name;
                windowsNames[id] = name;
                choices.Add(new Choice(id, Loc.Format("Automation_DeviceNotConnected", UsbDeviceNames.Label(id, name, customNames))));
            }
        }
    }
}
