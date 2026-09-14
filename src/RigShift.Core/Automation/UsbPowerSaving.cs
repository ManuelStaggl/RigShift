namespace RigShift.Core.Automation;

/// <summary>What the USB power-saving check could read for one device.</summary>
public sealed record UsbPowerFindings
{
    /// <summary>"USB selective suspend setting" of the active power scheme on AC; <c>null</c> when it could not be read.</summary>
    public bool? SelectiveSuspendEnabledOnAc { get; init; }

    /// <summary>Device instances of this vendor and product id Windows knows (one per port or serial number, plus interfaces).</summary>
    public int InstancesFound { get; init; }

    /// <summary>
    /// Instances whose <c>Device Parameters</c> allow power saving (<c>EnhancedPowerManagementEnabled</c> or
    /// <c>SelectiveSuspendEnabled</c> = 1) – what Device Manager shows as "Allow the computer to turn off this device".
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

    /// <summary>A registry flag as drivers store it: DWORD 1, or binary with a first byte of 1.</summary>
    public static bool IsFlagEnabled(object? registryValue) => registryValue switch
    {
        int value => value == 1,
        long value => value == 1,
        byte[] { Length: > 0 } bytes => bytes[0] == 1 && bytes.Skip(1).All(b => b == 0),
        _ => false,
    };
}
