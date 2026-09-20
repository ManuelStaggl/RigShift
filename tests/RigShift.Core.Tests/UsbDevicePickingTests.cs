using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>Which connected devices are offered as a trigger: what a user plugs in, not what is built into the PC.</summary>
public sealed class UsbDevicePickingTests
{
    [Fact]
    public void IsOffered_ExternalDevice_IsOffered()
    {
        UsbDevicePicking.IsOffered("FANATEC Wheel", "HIDClass", builtIn: false).ShouldBeTrue();
    }

    [Fact]
    public void IsOffered_Hub_IsNot()
    {
        UsbDevicePicking.IsOffered("Generic USB Hub", "USB", builtIn: false).ShouldBeFalse();
    }

    [Fact]
    public void IsOffered_BluetoothAdapter_IsNot_EvenAsADongle()
    {
        UsbDevicePicking.IsOffered("Intel(R) Wireless Bluetooth(R)", "Bluetooth", builtIn: false).ShouldBeFalse();
    }

    [Fact]
    public void IsOffered_BuiltIntoThePc_IsNot()
    {
        UsbDevicePicking.IsOffered("MYSTIC LIGHT", "HIDClass", builtIn: true).ShouldBeFalse();
    }

    [Fact]
    public void IsOffered_UnknownClass_IsOffered()
    {
        UsbDevicePicking.IsOffered("Button box", null, builtIn: false).ShouldBeTrue();
    }
}
