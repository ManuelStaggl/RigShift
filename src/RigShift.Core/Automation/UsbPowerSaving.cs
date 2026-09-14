namespace RigShift.Core.Automation;

/// <summary>What the USB power-saving check could read for one device.</summary>
public sealed record UsbPowerFindings
{
    /// <summary>"USB selective suspend setting" of the active power scheme on AC; <c>null</c> when it could not be read.</summary>
    public bool? SelectiveSuspendEnabledOnAc { get; init; }

    /// <summary>
    /// Connected device instances of this vendor and product id (one per port or serial number, plus interfaces). Ports
    /// where the device was plugged in earlier are not counted (analysis finding C-04).
    /// </summary>
    public int InstancesFound { get; init; }

    /// <summary>
    /// Instances whose registry flags allow power saving (<see cref="UsbPowerSaving.AllowsPowerSaving"/>) – what Device
    /// Manager shows as "Allow the computer to turn off this device".
    /// </summary>
    public int InstancesWithPowerSaving { get; init; }
}

/// <summary>Turns raw findings into "show the hint or not" (docs/PLAN.md, section 6, "Neu für 1.3", item 3).</summary>
public static class UsbPowerSaving
{
    /// <summary>
    /// Warn when the device allows power saving and the power scheme does not rule selective suspend out. The scheme
    /// setting alone does not warn: a device without the permission is never suspended by it.
    /// </summary>
    public static bool ShouldWarn(UsbPowerFindings findings)
    {
        ArgumentNullException.ThrowIfNull(findings);
        return findings.InstancesWithPowerSaving > 0 && findings.SelectiveSuspendEnabledOnAc != false;
    }

    /// <summary>Values under <c>Device Parameters</c>, written by the USB hub driver for the Power Management tab.</summary>
    public static IReadOnlyList<string> DeviceParameterFlags { get; } = ["EnhancedPowerManagementEnabled", "SelectiveSuspendEnabled"];

    /// <summary>
    /// Values under <c>Device Parameters\WDF</c>: devices with a KMDF or WinUSB vendor driver (Thrustmaster, Logitech G HUB)
    /// keep the Power Management checkbox there. Which of them flips is to be confirmed on real hardware.
    /// </summary>
    public static IReadOnlyList<string> WdfFlags { get; } = ["IdleInWorkingState", "UserSetDeviceIdleEnabled"];

    /// <summary>True when any flag of either key allows power saving; the readers return <c>null</c> for a missing value.</summary>
    public static bool AllowsPowerSaving(Func<string, object?> deviceParameter, Func<string, object?> wdfParameter)
    {
        ArgumentNullException.ThrowIfNull(deviceParameter);
        ArgumentNullException.ThrowIfNull(wdfParameter);
        return DeviceParameterFlags.Any(flag => IsFlagEnabled(deviceParameter(flag)))
            || WdfFlags.Any(flag => IsFlagEnabled(wdfParameter(flag)));
    }

    /// <summary>A registry flag as drivers store it: DWORD 1, or binary whose little-endian value is 1 (so <c>01 01</c> is not).</summary>
    public static bool IsFlagEnabled(object? registryValue) => registryValue switch
    {
        int value => value == 1,
        long value => value == 1,
        byte[] { Length: > 0 } bytes => bytes[0] == 1 && bytes.Skip(1).All(b => b == 0),
        _ => false,
    };
}
