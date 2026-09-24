using NSubstitute;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The USB rules of one profile, as the profile editor's trigger tab edits them.</summary>
public sealed class ProfileRulesEditorTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";
    private const string Pedals = "VID_0EB7&PID_0030";

    private static readonly IReadOnlyList<UsbDevice> Connected =
        [new UsbDevice(Wheelbase, "Wheelbase"), new UsbDevice(Dongle, "Dongle")];

    private readonly Profile _rig = Rig();

    [Fact]
    public void Load_TwoRulesForSameDevice_BothCardsWarn()
    {
        ProfileRulesEditor editor = Create(RuleFor(Wheelbase), RuleFor(Wheelbase), RuleFor(Dongle));

        editor.Rules.Select(r => r.HasDuplicateDevice).ShouldBe([true, true, false]);
    }

    [Fact]
    public void ChangingDeviceOfDuplicate_ClearsWarningOnBoth()
    {
        ProfileRulesEditor editor = Create(RuleFor(Wheelbase), RuleFor(Wheelbase));

        editor.Rules[1].Devices[0].SelectedDevice = editor.DeviceChoiceFor(Dongle);

        editor.Rules.ShouldAllBe(r => !r.HasDuplicateDevice);
    }

    [Fact]
    public void Load_SameDeviceSetWarns_SingleDeviceOfACombinationDoesNot()
    {
        ProfileRulesEditor editor = Create(RuleFor(Wheelbase), RuleFor(Wheelbase, Dongle), RuleFor(Dongle, Wheelbase));

        editor.Rules.Select(r => r.HasDuplicateDevice).ShouldBe([false, true, true]);
        editor.Rules[1].IsCombination.ShouldBeTrue();
        editor.Rules[0].IsCombination.ShouldBeFalse();
    }

    [Fact]
    public void AddDevice_PicksAnotherDevice_RemoveEmptiesTheRule()
    {
        ProfileRulesEditor editor = Create(RuleFor(Wheelbase));
        RuleCard card = editor.Rules[0];

        card.AddDeviceCommand.Execute(null);

        card.ToRule().Devices.ShouldNotBeNull().Select(d => d.Id).ShouldBe([Wheelbase, Dongle]);
        card.DevicesText.ShouldBe("Wheelbase + Dongle");

        card.RemoveDeviceCommand.Execute(card.Devices[0]);
        card.ToRule().Devices.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(Dongle);

        // Removing the last device keeps the rule with one empty slot (the trigger tab's chips allow that).
        card.RemoveDeviceCommand.Execute(card.Devices[0]);
        card.ToRule().Devices.ShouldNotBeNull().ShouldBeEmpty();
        card.HasDevices.ShouldBeFalse();
        card.Devices.ShouldHaveSingleItem().SelectedDevice.ShouldBeNull();
    }

    [Fact]
    public void RemoveRule_ClearsTheDuplicateWarningOfTheOther()
    {
        ProfileRulesEditor editor = Create(RuleFor(Wheelbase), RuleFor(Wheelbase));

        editor.RemoveRuleCommand.Execute(editor.Rules[1]);

        editor.Rules.ShouldHaveSingleItem().HasDuplicateDevice.ShouldBeFalse();
        editor.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void DeviceChoices_OfferKnownDevicesThatAreNotConnected()
    {
        // HW-08: the base was in a profile's app wait, but off – it must still be selectable.
        const string Button = "VID_1234&PID_0001";
        Profile desk = Profile("Desk", []) with { AppsWaitForUsbDeviceId = Pedals, AppsWaitForUsbDeviceName = "Pedals" };

        var editor = new ProfileRulesEditor(
            _rig.Id, [], [desk, _rig], null, Connected, new Dictionary<string, string> { [Button] = "Box" }, PowerCheck(), Logger.None);

        editor.DeviceChoiceFor(Pedals).ShouldNotBeNull().Name.ShouldContain("Pedals");
        editor.DeviceChoiceFor(Button).ShouldNotBeNull().Name.ShouldContain("Box");
    }

    [Fact]
    public void DeviceChoices_ShowTheCustomNameBesidesWindowsName()
    {
        var editor = new ProfileRulesEditor(
            _rig.Id, [RuleFor(Wheelbase)], [_rig], null, Connected, new Dictionary<string, string> { [Wheelbase] = "Wheel" }, PowerCheck(), Logger.None);

        editor.DeviceChoiceFor(Wheelbase).ShouldNotBeNull().Name.ShouldBe("Wheel · Wheelbase");
        editor.DescribeDevices([Wheelbase]).ShouldBe("Wheel");
    }

    [Fact]
    public void AddRule_WithAnotherDefaultProfile_EndsThere()
    {
        Profile desk = Profile("Desk", []);
        var editor = new ProfileRulesEditor(_rig.Id, [], [desk, _rig], desk.Id, Connected, null, PowerCheck(), Logger.None);

        editor.AddRuleCommand.Execute(null);

        AutomationRule rule = editor.Build().ShouldHaveSingleItem();
        rule.ProfileId.ShouldBe(_rig.Id);
        rule.OnExit.ShouldBe(ExitAction.SwitchTo);
        rule.ExitProfileId.ShouldBe(desk.Id);
    }

    [Fact]
    public void AddRule_AsTheOnlyProfile_SwitchesBack()
    {
        ProfileRulesEditor editor = Create();

        editor.AddRuleCommand.Execute(null);

        AutomationRule rule = editor.Build().ShouldHaveSingleItem();
        rule.OnExit.ShouldBe(ExitAction.SwitchBack);
        rule.ExitProfileId.ShouldBeNull();
    }

    [Fact]
    public void Merge_KeepsTheRulesOfOtherProfiles()
    {
        Profile desk = Profile("Desk", []);
        var other = new AutomationRule { Devices = [new RuleDevice { Id = Dongle }], ProfileId = desk.Id };
        var editor = new ProfileRulesEditor(
            _rig.Id, [other, RuleFor(Wheelbase)], [desk, _rig], null, Connected, null, PowerCheck(), Logger.None);

        editor.Rules.ShouldHaveSingleItem();
        editor.MergeInto([other, RuleFor(Wheelbase)]).Select(r => r.ProfileId).ShouldBe([desk.Id, _rig.Id]);
        editor.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void MergeInto_TakesTheOtherRulesAsTheyAreNow()
    {
        Profile desk = Profile("Desk", []);
        var other = new AutomationRule { Devices = [new RuleDevice { Id = Dongle }], ProfileId = desk.Id };
        var editor = new ProfileRulesEditor(_rig.Id, [RuleFor(Wheelbase)], [desk, _rig], null, Connected, null, PowerCheck(), Logger.None);

        // The assistant added a rule for Desk while this editor was open.
        editor.MergeInto([RuleFor(Wheelbase), other]).Select(r => r.ProfileId).ShouldBe([desk.Id, _rig.Id]);
    }

    [Fact]
    public void ChangedOnDisk_OnlyForThisProfilesRules()
    {
        Profile desk = Profile("Desk", []);
        AutomationRule own = RuleFor(Wheelbase);
        var other = new AutomationRule { Devices = [new RuleDevice { Id = Dongle }], ProfileId = desk.Id };
        var editor = new ProfileRulesEditor(_rig.Id, [own], [desk, _rig], null, Connected, null, PowerCheck(), Logger.None);

        editor.ChangedOnDisk([own, other]).ShouldBeFalse();
        editor.ChangedOnDisk([own with { SkipConfirmation = !own.SkipConfirmation }]).ShouldBeTrue();
        editor.ChangedOnDisk([]).ShouldBeTrue();
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

    private static IUsbPowerCheck PowerCheck()
    {
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        return powerCheck;
    }

    private AutomationRule RuleFor(params string[] devices) =>
        new() { Devices = [.. devices.Select(id => new RuleDevice { Id = id })], ProfileId = _rig.Id };

    private ProfileRulesEditor Create(params AutomationRule[] rules) =>
        new(_rig.Id, rules, [_rig], null, Connected, null, PowerCheck(), Logger.None);
}
