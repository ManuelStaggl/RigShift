using NSubstitute;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The settings' device group: the table and the pause switch that used to live on the automation page.</summary>
public sealed class UsbDevicesViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";
    private const string Pedals = "VID_0EB7&PID_0030";

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly Profile _rig = Rig();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NamedDevices_OnlyUsedDevices_ConnectedFirst()
    {
        // The dongle is connected but no rule uses it: the table stays short instead of listing every hub.
        UsbDevicesViewModel devices = await CreateAsync(
            new AutomationRule
            {
                Devices = [new RuleDevice { Id = Pedals, Name = "Pedals" }, new RuleDevice { Id = Wheelbase }],
                ProfileId = _rig.Id,
            });

        devices.NamedDevices.Select(n => n.WindowsName).ShouldBe(["Wheelbase", "Pedals"]);
        devices.NamedDevices.Select(n => n.Id).ShouldNotContain(Dongle);
        devices.HasNoNamedDevices.ShouldBeFalse();
    }

    [Fact]
    public async Task NamedDevices_WithoutRules_IsEmpty()
    {
        UsbDevicesViewModel devices = await CreateAsync();

        devices.NamedDevices.ShouldBeEmpty();
        devices.HasNoNamedDevices.ShouldBeTrue();
    }

    [Fact]
    public async Task NamedDevices_ListsADeviceAProfileWaitsFor()
    {
        // HW-08: the device of a profile's app wait can be named although no rule mentions it.
        _host.Store.Profiles.Add(Profile("Desk", []) with { AppsWaitForUsbDeviceId = Pedals, AppsWaitForUsbDeviceName = "Pedals" });

        UsbDevicesViewModel devices = await CreateAsync();

        devices.NamedDevices.ShouldHaveSingleItem().Id.ShouldBe(Pedals);
    }

    [Fact]
    public async Task RenameDevice_IsSavedAndShownInTheTable()
    {
        UsbDevicesViewModel devices = await CreateAsync(RuleFor(Wheelbase));
        UsbNameCard card = devices.NamedDevices.First(n => n.Id == Wheelbase);

        card.CustomName = "Wheel";
        await card.SaveNameAsync();

        _host.Settings.Current.UsbDeviceNames.ShouldNotBeNull()[Wheelbase].ShouldBe("Wheel");
        devices.ErrorMessage.ShouldBeNull();
        devices.NamedDevices.First(n => n.Id == Wheelbase).CustomName.ShouldBe("Wheel");
    }

    [Fact]
    public async Task RenameDevice_EmptyName_RemovesIt()
    {
        UsbDevicesViewModel devices = await CreateAsync(RuleFor(Wheelbase));
        UsbNameCard card = devices.NamedDevices.First(n => n.Id == Wheelbase);
        card.CustomName = "Wheel";
        await card.SaveNameAsync();

        card.CustomName = "   ";
        await card.SaveNameAsync();

        (_host.Settings.Current.UsbDeviceNames?.ContainsKey(Wheelbase) ?? false).ShouldBeFalse();
    }

    [Fact]
    public async Task Pause_IsSavedAndFollowsTheServiceBack()
    {
        UsbDevicesViewModel devices = await CreateAsync(RuleFor(Wheelbase));

        devices.IsPaused = true;
        await Task.Yield();

        _host.Settings.Current.AutomationPaused.ShouldBeTrue();
        devices.IsPaused.ShouldBeTrue();
    }

    public void Dispose() => _host.Dispose();

    private AutomationRule RuleFor(params string[] devices) =>
        new() { Devices = [.. devices.Select(id => new RuleDevice { Id = id })], ProfileId = _rig.Id };

    private async Task<UsbDevicesViewModel> CreateAsync(params AutomationRule[] rules)
    {
        _host.Usb.PresentDeviceIds().Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Wheelbase, "Wheelbase"), new UsbDevice(Dongle, "Dongle")]);
        _host.Store.Profiles.Add(_rig);
        await _host.Catalog.ReloadAsync(Ct);
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = rules }, Ct);

        var automation = new AutomationService(
            _host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, _host.Fullscreen, TimeProvider.System, Logger.None);
        var devices = new UsbDevicesViewModel(_host.Settings, _host.Catalog, automation, _host.Usb, Logger.None);
        devices.Refresh();
        return devices;
    }
}
