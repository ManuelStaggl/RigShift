using System.IO;
using System.Security;
using Microsoft.Win32;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using Serilog;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Power;

/// <summary>
/// <see cref="IUsbPowerCheck"/>, read-only: the "USB selective suspend setting" of the active power scheme on AC, and the
/// power flags in <c>HKLM\SYSTEM\CurrentControlSet\Enum\USB\&lt;VID_xxxx&amp;PID_xxxx…&gt;\&lt;instance&gt;\Device Parameters</c>.
/// </summary>
public sealed class UsbPowerCheck : IUsbPowerCheck
{
    private const string UsbEnumKey = @"SYSTEM\CurrentControlSet\Enum\USB";

    private static readonly Guid UsbSettingsSubgroup = new("2a737441-1930-4402-8d77-b2bebba308a3");
    private static readonly Guid SelectiveSuspendSetting = new("48e6b7a6-50f5-4782-a5d4-53bb8f07e226");
    private static readonly string[] PowerFlags = ["EnhancedPowerManagementEnabled", "SelectiveSuspendEnabled"];

    private readonly ILogger _log;

    public UsbPowerCheck(ILogger log)
    {
        ArgumentNullException.ThrowIfNull(log);
        _log = log.ForContext<UsbPowerCheck>();
    }

    public UsbPowerFindings Check(string usbDeviceId)
    {
        (int found, int withPowerSaving) = UsbDeviceIds.Normalize(usbDeviceId) is { } id ? ReadInstances(id) : (0, 0);
        var findings = new UsbPowerFindings
        {
            SelectiveSuspendEnabledOnAc = ReadSelectiveSuspend(),
            InstancesFound = found,
            InstancesWithPowerSaving = withPowerSaving,
        };
        _log.Debug("USB power check for {Device}: {@Findings}", usbDeviceId, findings);
        return findings;
    }

    private unsafe bool? ReadSelectiveSuspend()
    {
        WIN32_ERROR result = PInvoke.PowerGetActiveScheme(null, out Guid* scheme);
        if (result != WIN32_ERROR.ERROR_SUCCESS)
        {
            _log.Debug("PowerGetActiveScheme failed with {Error}", result);
            return null;
        }

        try
        {
            result = PInvoke.PowerReadACValueIndex(null, *scheme, UsbSettingsSubgroup, SelectiveSuspendSetting, out uint value);
            if (result != WIN32_ERROR.ERROR_SUCCESS)
            {
                _log.Debug("PowerReadACValueIndex for USB selective suspend failed with {Error}", result);
                return null;
            }

            return value == 1;
        }
        finally
        {
            PInvoke.LocalFree(new HLOCAL(scheme));
        }
    }

    private (int Found, int WithPowerSaving) ReadInstances(string deviceId)
    {
        int found = 0;
        int withPowerSaving = 0;
        try
        {
            using RegistryKey? usb = Registry.LocalMachine.OpenSubKey(UsbEnumKey);
            if (usb is null)
            {
                return (0, 0);
            }

            // Interfaces of composite devices (&MI_xx) are separate keys with their own flags.
            foreach (string deviceKey in usb.GetSubKeyNames().Where(k => string.Equals(UsbDeviceIds.Normalize(k), deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                using RegistryKey? device = usb.OpenSubKey(deviceKey);
                foreach (string instance in device?.GetSubKeyNames() ?? [])
                {
                    found++;
                    if (HasPowerSaving(device!, instance))
                    {
                        withPowerSaving++;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "USB device keys for {Device} could not be read", deviceId);
        }

        return (found, withPowerSaving);
    }

    private bool HasPowerSaving(RegistryKey device, string instance)
    {
        try
        {
            using RegistryKey? parameters = device.OpenSubKey(instance + @"\Device Parameters");
            return parameters is not null && PowerFlags.Any(flag => UsbPowerSaving.IsFlagEnabled(parameters.GetValue(flag)));
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException or IOException)
        {
            _log.Debug(ex, "Device parameters of {Instance} could not be read", instance);
            return false;
        }
    }
}
