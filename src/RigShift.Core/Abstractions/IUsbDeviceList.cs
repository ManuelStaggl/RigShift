using RigShift.Core.Automation;

namespace RigShift.Core.Abstractions;

/// <summary>OS boundary for USB triggers: which devices are connected right now (docs/PLAN.md, section 6, item 7).</summary>
public interface IUsbDeviceList
{
    /// <summary>Connected devices as <c>VID_xxxx&amp;PID_xxxx</c>, compared without case. Cheap enough to poll.</summary>
    IReadOnlySet<string> PresentDeviceIds();

    /// <summary>Connected devices with a name for picking one, one entry per vendor and product id, without hubs.</summary>
    IReadOnlyList<UsbDevice> ConnectedDevices();
}
