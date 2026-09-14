using RigShift.Core.Automation;

namespace RigShift.Core.Abstractions;

/// <summary>
/// Read-only check whether Windows may power down a USB device to save energy (docs/usb-power-saving.md). Never changes
/// a system setting.
/// </summary>
public interface IUsbPowerCheck
{
    /// <summary>Raw findings for a device id (<c>VID_xxxx&amp;PID_xxxx</c>); unreadable parts stay unknown instead of throwing.</summary>
    UsbPowerFindings Check(string usbDeviceId);
}
