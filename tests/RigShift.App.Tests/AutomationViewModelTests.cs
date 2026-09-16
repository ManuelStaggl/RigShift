using NSubstitute;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class AutomationViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly Profile _rig = Rig();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_TwoRulesForSameDevice_BothCardsWarn()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase), RuleFor(Dongle));

        viewModel.Rules.Select(r => r.HasDuplicateDevice).ShouldBe([true, true, false]);
    }

    [Fact]
    public async Task ChangingDeviceOfDuplicate_ClearsWarningOnBoth()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase));

        viewModel.Rules[1].Devices[0].SelectedDevice = viewModel.DeviceChoiceFor(Dongle);
        await viewModel.PendingSave;

        viewModel.Rules.ShouldAllBe(r => !r.HasDuplicateDevice);
    }

    [Fact]
    public async Task Load_SameDeviceSetWarns_SingleDeviceOfACombinationDoesNot()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase, Dongle), RuleFor(Dongle, Wheelbase));

        viewModel.Rules.Select(r => r.HasDuplicateDevice).ShouldBe([false, true, true]);
        viewModel.Rules[1].IsCombination.ShouldBeTrue();
        viewModel.Rules[0].IsCombination.ShouldBeFalse();
    }

    [Fact]
    public async Task AddDevice_PicksAnotherDevice_RemoveEmptiesTheRule()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase));
        RuleCard card = viewModel.Rules[0];

        card.AddDeviceCommand.Execute(null);

        card.ToRule().Devices.ShouldNotBeNull().Select(d => d.Id).ShouldBe([Wheelbase, Dongle]);
        card.DevicesText.ShouldBe("Wheelbase + Dongle");

        card.RemoveDeviceCommand.Execute(card.Devices[0]);
        await viewModel.PendingSave;
        card.ToRule().Devices.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(Dongle);

        // Removing the last device keeps the rule with one empty slot (the trigger tab's chips allow that).
        card.RemoveDeviceCommand.Execute(card.Devices[0]);
        await viewModel.PendingSave;
        card.ToRule().Devices.ShouldNotBeNull().ShouldBeEmpty();
        card.HasDevices.ShouldBeFalse();
        card.Devices.ShouldHaveSingleItem().SelectedDevice.ShouldBeNull();
    }

    [Fact]
    public async Task RenameDevice_RelabelsListsAndKeepsEachRulesDevice()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Dongle));
        string? askedFor = null;
        viewModel.ConfirmDeleteRule = name =>
        {
            askedFor = name;
            return Task.FromResult(false);
        };

        await viewModel.RenameDeviceAsync(viewModel.NamedDevices.First(n => n.Id == Wheelbase), "Wheel");

        viewModel.DeviceChoiceFor(Wheelbase).ShouldNotBeNull().Name.ShouldBe("Wheel · Wheelbase");
        viewModel.Rules.Select(r => r.Devices[0].SelectedDevice?.Key).ShouldBe([Wheelbase, Dongle]);
        _host.Settings.Current.UsbDeviceNames.ShouldNotBeNull()[Wheelbase].ShouldBe("Wheel");
        await viewModel.DeleteRuleCommand.ExecuteAsync(viewModel.Rules[0]);
        askedFor.ShouldBe("Wheel");
    }

    [Fact]
    public async Task NamedDevices_OnlyUsedDevices_ConnectedFirst()
    {
        const string Pedals = "VID_0EB7&PID_0030";
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Pedals, Wheelbase) with { Devices = [new RuleDevice { Id = Pedals, Name = "Pedals" }, new RuleDevice { Id = Wheelbase }] });

        viewModel.NamedDevices.Select(n => n.WindowsName).ShouldBe(["Wheelbase", "Pedals"]);

        viewModel.Rules[0].AddDeviceCommand.Execute(null);
        await viewModel.PendingSave;
        viewModel.NamedDevices.Select(n => n.Id).ShouldContain(Dongle);
        viewModel.DeviceChoiceFor(Pedals).ShouldNotBeNull().Name.ShouldContain("Pedals");
        viewModel.Rules[0].Devices[0].SelectedDevice.ShouldNotBeNull().Key.ShouldBe(Pedals);
    }

    [Fact]
    public async Task DeviceChoices_OfferKnownDevicesThatAreNotConnected()
    {
        // HW-08: the base was in a profile's app wait, but off – it must still be selectable.
        const string Pedals = "VID_0EB7&PID_0030";
        const string Button = "VID_1234&PID_0001";
        _host.Store.Profiles.Add(Profile("Desk", []) with { AppsWaitForUsbDeviceId = Pedals, AppsWaitForUsbDeviceName = "Pedals" });
        await _host.Settings.UpdateAsync(s => s with { UsbDeviceNames = new Dictionary<string, string> { [Button] = "Box" } }, Ct);

        AutomationViewModel viewModel = await CreateAsync();

        viewModel.DeviceChoiceFor(Pedals).ShouldNotBeNull().Name.ShouldContain("Pedals");
        viewModel.DeviceChoiceFor(Button).ShouldNotBeNull().Name.ShouldContain("Box");
    }

    [Fact]
    public async Task DeletingDuplicate_ClearsWarning()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase));

        await viewModel.DeleteRuleCommand.ExecuteAsync(viewModel.Rules[1]);

        viewModel.Rules.ShouldHaveSingleItem().HasDuplicateDevice.ShouldBeFalse();
    }

    [Fact]
    public async Task DeleteRule_Cancelled_KeepsRule()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase));
        string? askedFor = null;
        viewModel.ConfirmDeleteRule = name =>
        {
            askedFor = name;
            return Task.FromResult(false);
        };

        await viewModel.DeleteRuleCommand.ExecuteAsync(viewModel.Rules[0]);

        askedFor.ShouldBe("Wheelbase");
        viewModel.Rules.ShouldHaveSingleItem();
        _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
    }

    [Fact]
    public void NewRuleProfiles_WithDefaultProfile_EndsThereAndStartsWithAnother()
    {
        Profile desk = Profile("Desk", []);
        Profile tv = Profile("TV", []);

        RuleExits.NewRuleProfiles([tv, desk, _rig], desk.Id).ShouldBe((tv.Id, desk.Id));
    }

    [Fact]
    public void NewRuleProfiles_WithoutDefaultProfile_EndsAtFirstProfile()
    {
        Profile desk = Profile("Desk", []);

        RuleExits.NewRuleProfiles([desk, _rig], null).ShouldBe((_rig.Id, desk.Id));
        RuleExits.NewRuleProfiles([_rig], Guid.NewGuid()).ShouldBe((_rig.Id, _rig.Id));
    }

    [Fact]
    public async Task AddRule_EndActionSwitchesToProfile()
    {
        AutomationViewModel viewModel = await CreateAsync();

        await viewModel.AddRuleCommand.ExecuteAsync(null);

        AutomationRule rule = _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        rule.OnExit.ShouldBe(ExitAction.SwitchTo);
        rule.ExitProfileId.ShouldBe(_rig.Id);
    }

    public void Dispose() => _host.Dispose();

    private AutomationRule RuleFor(params string[] devices) =>
        new() { Devices = [.. devices.Select(id => new RuleDevice { Id = id })], ProfileId = _rig.Id };

    private async Task<AutomationViewModel> CreateAsync(params AutomationRule[] rules)
    {
        _host.Usb.PresentDeviceIds().Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Wheelbase, "Wheelbase"), new UsbDevice(Dongle, "Dongle")]);
        _host.Store.Profiles.Add(_rig);
        await _host.Catalog.ReloadAsync(Ct);
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = rules }, Ct);

        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        var automation = new AutomationService(_host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, _host.Fullscreen, TimeProvider.System, Logger.None);
        var viewModel = new AutomationViewModel(_host.Settings, _host.Catalog, automation, _host.Usb, powerCheck, Logger.None)
        {
            ConfirmDeleteRule = _ => Task.FromResult(true),
        };
        viewModel.Load();
        return viewModel;
    }
}
