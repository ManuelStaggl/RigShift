namespace RigShift.Core.Automation;

/// <summary>
/// Which connected USB devices a picker offers as a trigger. Only the offer is narrowed: a rule that already names a
/// device keeps working whatever this says.
/// </summary>
public static class UsbDevicePicking
{
    /// <param name="name">Windows' name of the device.</param>
    /// <param name="deviceClass">The setup class ("Bluetooth", "HIDClass", ...); <c>null</c> when unknown.</param>
    /// <param name="builtIn">Windows counts the device as part of the PC itself (mainboard lighting, an onboard adapter).</param>
    public static bool IsOffered(string name, string? deviceClass, bool builtIn)
    {
        ArgumentNullException.ThrowIfNull(name);

        return !builtIn
            && !name.Contains("hub", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(deviceClass, "Bluetooth", StringComparison.OrdinalIgnoreCase);
    }
}
