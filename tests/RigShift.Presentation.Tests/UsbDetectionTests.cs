using NSubstitute;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class UsbDetectionTests
{
    private static readonly UsbDevice Keyboard = new("USB\\VID_046D&PID_C31C", "USB input device");
    private static readonly UsbDevice Wheel = new("USB\\VID_0EB7&PID_0006", "USB input device");

    private readonly IUsbDeviceList _usb = Substitute.For<IUsbDeviceList>();

    [Fact]
    public async Task ADeviceSwitchedOffAndOn_IsFound_AndSoIsANewOne()
    {
        _usb.ConnectedDevices().Returns([Keyboard, Wheel], [Keyboard], [Keyboard, Wheel]);
        (await new UsbDetection(_usb, new AutoAdvanceTimeProvider()).DetectAsync()).ShouldBe(Wheel);

        _usb.ConnectedDevices().Returns([Keyboard], [Keyboard], [Keyboard, Wheel]);
        (await new UsbDetection(_usb, new AutoAdvanceTimeProvider()).DetectAsync()).ShouldBe(Wheel);
    }

    [Fact]
    public async Task NothingTurningUp_EndsAfterAMinute_AndSaysSo()
    {
        _usb.ConnectedDevices().Returns([Keyboard]);
        var time = new AutoAdvanceTimeProvider();
        var detection = new UsbDetection(_usb, time);

        (await detection.DetectAsync()).ShouldBeNull();

        time.Elapsed.ShouldBeGreaterThanOrEqualTo(UsbDetection.Limit);
        detection.FoundNothing.ShouldBeTrue();
        detection.IsDetecting.ShouldBeFalse();
    }

    [Fact]
    public async Task BothPickers_TakeTheDetectedDevice()
    {
        _usb.ConnectedDevices().Returns([Keyboard], [Keyboard, Wheel]);
        var wait = new AppsWaitDeviceChoice(null, null, [Keyboard], [], null, new UsbDetection(_usb, new AutoAdvanceTimeProvider()));

        await wait.DetectCommand.ExecuteAsync(null);

        wait.DeviceId.ShouldBe(Wheel.Id);

        _usb.ConnectedDevices().Returns([Keyboard], [Keyboard, Wheel]);
        IUsbPowerCheck power = Substitute.For<IUsbPowerCheck>();
        power.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        var profileId = Guid.NewGuid();
        var rules = new ProfileRulesEditor(
            profileId, [new AutomationRule { ProfileId = profileId }], [], null, [Keyboard], null, power, Logger.None,
            new UsbDetection(_usb, new AutoAdvanceTimeProvider()));

        await rules.Rules[0].DetectDeviceCommand.ExecuteAsync(null);

        rules.Build().Single().Devices.ShouldNotBeNull().Select(d => d.Id).ShouldBe([Wheel.Id]);
    }
}
