using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class UsbDeviceNamesTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Pedals = "VID_0EB7&PID_0030";

    private static readonly Dictionary<string, string> Names = new() { ["vid_0eb7&pid_0020"] = "  Wheel " };

    [Fact]
    public void NameOf_PrefersCustomName_ThenWindowsName_ThenId()
    {
        UsbDeviceNames.NameOf(@"USB\VID_0EB7&PID_0020\5&1", "CSL DD", Names).ShouldBe("Wheel");
        UsbDeviceNames.NameOf(Pedals, "Pedals", Names).ShouldBe("Pedals");
        UsbDeviceNames.NameOf(Pedals, null, null).ShouldBe(Pedals);
    }

    [Fact]
    public void Label_ShowsCustomAndWindowsName()
    {
        UsbDeviceNames.Label(Wheelbase, "CSL DD", Names).ShouldBe("Wheel · CSL DD");
        UsbDeviceNames.Label(Wheelbase, "Wheel", Names).ShouldBe("Wheel");
        UsbDeviceNames.Label(Pedals, "Pedals", Names).ShouldBe("Pedals");
    }

    [Fact]
    public void Describe_JoinsTheDevicesOfARule()
    {
        var rule = new AutomationRule { Devices = [new RuleDevice { Id = Wheelbase }, new RuleDevice { Id = Pedals, Name = "Pedals" }, new RuleDevice { Id = "" }] };

        UsbDeviceNames.Describe(rule, Names).ShouldBe("Wheel + Pedals");
        UsbDeviceNames.Describe(new AutomationRule { Devices = [] }, Names).ShouldBeNull();
        UsbDeviceNames.Describe(new AutomationRule { LegacyUsbDeviceId = Pedals, LegacyUsbDeviceName = "Old pedals" }, null).ShouldBe("Old pedals");
    }

    [Fact]
    public void WithName_SetsNormalizedAndRemovesBlank()
    {
        IReadOnlyDictionary<string, string> named = UsbDeviceNames.WithName(Names, @"USB\VID_0eb7&PID_0030\7&2", new string('x', 60));

        named[Pedals].Length.ShouldBe(UsbDeviceNames.MaxCustomNameLength);
        named[Wheelbase].ShouldBe("Wheel");
        UsbDeviceNames.WithName(named, Wheelbase, "  ").ContainsKey(Wheelbase).ShouldBeFalse();
        Should.Throw<ArgumentException>(() => UsbDeviceNames.WithName(null, "no id", "x"));
    }
}
