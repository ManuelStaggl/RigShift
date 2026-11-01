using System.ComponentModel;
using System.Text;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using Windows.Win32;
using Windows.Win32.Devices.DeviceAndDriverInstallation;
using Windows.Win32.Devices.Properties;
using Windows.Win32.Foundation;

namespace RigShift.Windows.Apps;

/// <summary>
/// <see cref="IUsbDeviceList"/> over the configuration manager: the present device instances below the USB enumerator.
/// Polled instead of device notifications, so games and devices share one path.
/// </summary>
public sealed class UsbDeviceList : IUsbDeviceList
{
    private const string Enumerator = "USB";

    private static readonly DEVPROPKEY[] NameKeys =
    [
        PInvoke.DEVPKEY_Device_BusReportedDeviceDesc,
        PInvoke.DEVPKEY_Device_FriendlyName,
        PInvoke.DEVPKEY_Device_DeviceDesc,
    ];

    public IReadOnlySet<string> PresentDeviceIds()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string instance in PresentInstanceIds())
        {
            if (UsbDeviceIds.Normalize(instance) is { } id)
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    public IReadOnlyList<UsbDevice> ConnectedDevices()
    {
        var devices = new Dictionary<string, UsbDevice>(StringComparer.OrdinalIgnoreCase);

        // Composite devices list each interface (&MI_xx) as well; the device itself carries the better name, so it goes first.
        foreach (string instance in PresentInstanceIds().OrderBy(i => i.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
        {
            if (UsbDeviceIds.Normalize(instance) is not { } id || devices.ContainsKey(id))
            {
                continue;
            }

            if (Describe(instance) is { } found && UsbDevicePicking.IsOffered(found.Name, found.DeviceClass, found.BuiltIn))
            {
                devices[id] = new UsbDevice(id, found.Name);
            }
        }

        return devices.Values.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Instance ids (<c>USB\VID_xxxx&amp;PID_xxxx\&lt;instance&gt;</c>) of the connected USB devices.</summary>
    internal static unsafe List<string> PresentInstanceIds()
    {
        const uint Flags = PInvoke.CM_GETIDLIST_FILTER_ENUMERATOR | PInvoke.CM_GETIDLIST_FILTER_PRESENT;

        // A device can arrive between the size query and the list itself; retry on a too-small buffer.
        for (int attempt = 0; attempt < 3; attempt++)
        {
            CONFIGRET sizeResult = PInvoke.CM_Get_Device_ID_List_Size(out uint length, Enumerator, Flags);
            if (sizeResult != CONFIGRET.CR_SUCCESS)
            {
                throw new Win32Exception($"CM_Get_Device_ID_List_Size failed with {sizeResult}.");
            }

            char[] buffer = new char[length];
            CONFIGRET result;
            fixed (char* chars = buffer)
            {
                result = PInvoke.CM_Get_Device_ID_List(Enumerator, new PZZWSTR(chars), length, Flags);
            }

            if (result == CONFIGRET.CR_BUFFER_SMALL)
            {
                continue;
            }

            if (result != CONFIGRET.CR_SUCCESS)
            {
                throw new Win32Exception($"CM_Get_Device_ID_List failed with {result}.");
            }

            return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries).ToList();
        }

        throw new Win32Exception("CM_Get_Device_ID_List kept reporting a too-small buffer.");
    }

    private static unsafe (string Name, string? DeviceClass, bool BuiltIn)? Describe(string instanceId)
    {
        uint node;
        fixed (char* id = instanceId)
        {
            if (PInvoke.CM_Locate_DevNode(out node, new PWSTR(id), CM_LOCATE_DEVNODE_FLAGS.CM_LOCATE_DEVNODE_NORMAL) != CONFIGRET.CR_SUCCESS)
            {
                return null;
            }
        }

        foreach (DEVPROPKEY key in NameKeys)
        {
            if (StringProperty(node, key) is { Length: > 0 } name)
            {
                return (name, StringProperty(node, PInvoke.DEVPKEY_Device_Class), BoolProperty(node, PInvoke.DEVPKEY_Device_InLocalMachineContainer));
            }
        }

        return null;
    }

    /// <summary>False when the property is missing: an unknown device is rather offered than hidden.</summary>
    private static unsafe bool BoolProperty(uint node, DEVPROPKEY key)
    {
        DEVPROPTYPE type;
        sbyte value = 0;
        uint size = sizeof(sbyte);
        return PInvoke.CM_Get_DevNode_Property(node, &key, &type, (byte*)&value, &size, 0) == CONFIGRET.CR_SUCCESS
            && type == DEVPROPTYPE.DEVPROP_TYPE_BOOLEAN
            && value != 0;
    }

    private static unsafe string? StringProperty(uint node, DEVPROPKEY key)
    {
        DEVPROPTYPE type;
        uint size = 0;
        if (PInvoke.CM_Get_DevNode_Property(node, &key, &type, null, &size, 0) != CONFIGRET.CR_BUFFER_SMALL || size == 0)
        {
            return null;
        }

        byte[] buffer = new byte[size];
        fixed (byte* bytes = buffer)
        {
            if (PInvoke.CM_Get_DevNode_Property(node, &key, &type, bytes, &size, 0) != CONFIGRET.CR_SUCCESS || type != DEVPROPTYPE.DEVPROP_TYPE_STRING)
            {
                return null;
            }
        }

        // Bus-reported names are often padded with spaces.
        return Encoding.Unicode.GetString(buffer, 0, (int)size).TrimEnd('\0').Trim();
    }
}
