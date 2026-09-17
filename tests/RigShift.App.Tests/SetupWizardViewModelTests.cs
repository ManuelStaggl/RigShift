using NSubstitute;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class SetupWizardViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";

    private static readonly AudioEndpoint Speakers = new("speakers", "Speakers");
    private static readonly AudioEndpoint Headset = new("headset", "Headset");

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly IAudioController _audio = Substitute.For<IAudioController>();
    private readonly SetupWizardViewModel _viewModel;

    public SetupWizardViewModelTests()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>()).Returns(
        [
            new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, IsDefault: true),
            new AudioDeviceInfo(Headset, AudioDirection.Render, IsActive: true, IsDefault: false),
        ]);
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Dongle, "Dongle")]);
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());

        _viewModel = new SetupWizardViewModel(
            _host.Catalog, _host.Display, _audio, _host.Usb, powerCheck,
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), _host.Settings, Logger.None);
    }

    private static DisplaySnapshot RigActive() => Snapshot(
        Attached(Desk4K),
        Attached(DeskLeft),
        Attached(DeskRight),
        Attached(Ultrawide, activeMode: UltrawideMode));

    [Fact]
    public async Task FirstStep_SavesActiveDisplaysAndDefaultPlayback()
    {
        await _viewModel.StartCommand.ExecuteAsync(null);

        _viewModel.Step.ShouldBe(SetupStep.First);
        _viewModel.DisplayLines.Count.ShouldBe(3);
        _viewModel.Playback.ShouldNotBeNull().Endpoint.ShouldBe(Speakers);
        _viewModel.ProfileName.ShouldNotBeNullOrWhiteSpace();
        _viewModel.HasNameProblem.ShouldBeFalse();
        _viewModel.SaveProfileCommand.CanExecute(null).ShouldBeTrue();

        _viewModel.ProfileName = "Desk";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);

        Profile desk = _host.Catalog.Profiles.ShouldHaveSingleItem();
        desk.Name.ShouldBe("Desk");
        desk.Displays.Count.ShouldBe(3);
        desk.Audio.Playback.ShouldBe(Speakers);
        desk.Icon.ShouldBe(ProfileIcons.Desk);
        _viewModel.Step.ShouldBe(SetupStep.Second);
    }

    [Fact]
    public async Task SecondStep_SameArrangementAsFirst_CannotSaveUntilDisplaysChange()
    {
        await SaveFirstAsync();

        _viewModel.MatchesFirst.ShouldBeTrue();
        _viewModel.SaveProfileCommand.CanExecute(null).ShouldBeFalse();

        _host.Display.SetSnapshot(RigActive());
        await _viewModel.RefreshDisplaysAsync();
        _viewModel.MatchesFirst.ShouldBeFalse();
        _viewModel.SaveProfileCommand.CanExecute(null).ShouldBeTrue();
        _viewModel.Playback.ShouldNotBeNull().Selected = _viewModel.Playback.Choices.First(c => c.Endpoint == Headset);
        _viewModel.ProfileName = "Rig";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);

        Profile rig = _host.Catalog.Profiles.Single(p => p.Name == "Rig");
        rig.Displays.ShouldHaveSingleItem().Identity.ShouldBe(Ultrawide);
        rig.Audio.Playback.ShouldBe(Headset);
        rig.Icon.ShouldBe(ProfileIcons.Rig);
        _viewModel.Step.ShouldBe(SetupStep.Trigger);
    }

    [Fact]
    public async Task ProfileStep_NameOfExistingProfile_CannotSave()
    {
        await SaveFirstAsync();
        _host.Display.SetSnapshot(RigActive());
        await _viewModel.RefreshDisplaysAsync();

        _viewModel.ProfileName = " desk ";

        _viewModel.HasNameProblem.ShouldBeTrue();
        _viewModel.SaveProfileCommand.CanExecute(null).ShouldBeFalse();
    }

    [Fact]
    public async Task Trigger_DeviceTurnedOn_IsPickedAndRuleSwitchesBetweenBothProfiles()
    {
        await SaveBothAsync();

        _viewModel.SelectedDevice.ShouldBeNull();
        _viewModel.HasDetected.ShouldBeFalse();

        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Dongle, "Dongle"), new UsbDevice(Wheelbase, "Wheelbase")]);
        _viewModel.PollUsb();

        _viewModel.SelectedDevice.ShouldNotBeNull().Key.ShouldBe(Wheelbase);
        _viewModel.HasDetected.ShouldBeTrue();
        _viewModel.CreateRuleCommand.CanExecute(null).ShouldBeTrue();

        await _viewModel.CreateRuleCommand.ExecuteAsync(null);

        AutomationRule rule = _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        rule.Devices.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe(Wheelbase);
        rule.ProfileId.ShouldBe(_viewModel.SecondProfile.ShouldNotBeNull().Id);
        rule.OnExit.ShouldBe(ExitAction.SwitchTo);
        rule.ExitProfileId.ShouldBe(_viewModel.FirstProfile.ShouldNotBeNull().Id);
        _viewModel.Step.ShouldBe(SetupStep.Done);
    }

    [Fact]
    public async Task Trigger_NoDeviceAtFirstLook_DeviceTurnedOnLaterIsStillDetected()
    {
        _host.Usb.ConnectedDevices().Returns([]);
        await SaveBothAsync();

        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Wheelbase, "Wheelbase")]);
        _viewModel.PollUsb();

        _viewModel.SelectedDevice.ShouldNotBeNull().Key.ShouldBe(Wheelbase);
    }

    [Fact]
    public async Task Trigger_Skipped_CreatesNoRule()
    {
        await SaveBothAsync();

        _viewModel.SkipTriggerCommand.Execute(null);

        _viewModel.Step.ShouldBe(SetupStep.Done);
        _host.Settings.Current.AutomationRules.ShouldBeNull();
    }

    [Fact]
    public async Task Back_FromSecondStep_EditsTheFirstProfileInsteadOfAddingOne()
    {
        await SaveFirstAsync();
        _host.Display.SetSnapshot(RigActive());
        await _viewModel.RefreshDisplaysAsync();

        await _viewModel.BackCommand.ExecuteAsync(null);

        _viewModel.Step.ShouldBe(SetupStep.First);
        _viewModel.ProfileName.ShouldBe("Desk");
        // Its own name is no longer taken by another profile, so the step can be saved again.
        _viewModel.HasNameProblem.ShouldBeFalse();
        _viewModel.SaveProfileCommand.CanExecute(null).ShouldBeTrue();

        _viewModel.ProfileName = "Desk 2";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);

        Profile desk = _host.Catalog.Profiles.ShouldHaveSingleItem();
        desk.Name.ShouldBe("Desk 2");
        desk.Displays.ShouldHaveSingleItem().Identity.ShouldBe(Ultrawide);
        _viewModel.Step.ShouldBe(SetupStep.Second);
    }

    [Fact]
    public async Task Back_FromDone_RemovesTheRuleItCreated()
    {
        await SaveBothAsync();
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Dongle, "Dongle"), new UsbDevice(Wheelbase, "Wheelbase")]);
        _viewModel.PollUsb();
        await _viewModel.CreateRuleCommand.ExecuteAsync(null);
        _viewModel.Step.ShouldBe(SetupStep.Done);

        await _viewModel.BackCommand.ExecuteAsync(null);

        _viewModel.Step.ShouldBe(SetupStep.Trigger);
        _viewModel.CreatedRule.ShouldBeNull();
        _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldBeEmpty();
    }

    [Fact]
    public async Task Back_OnTheWelcomeStep_IsNotOffered()
    {
        _viewModel.CanGoBack.ShouldBeFalse();
        _viewModel.BackCommand.CanExecute(null).ShouldBeFalse();

        await _viewModel.StartCommand.ExecuteAsync(null);

        _viewModel.CanGoBack.ShouldBeTrue();
    }

    [Fact]
    public async Task StepList_MarksTheStepsBehindAsDoneAndTheCurrentOne()
    {
        await SaveFirstAsync();

        _viewModel.StepList.Count.ShouldBe(5);
        _viewModel.StepList.Select(s => s.IsDone).ShouldBe([true, true, false, false, false]);
        _viewModel.StepList.Select(s => s.IsCurrent).ShouldBe([false, false, true, false, false]);
        _viewModel.StepList.Select(s => s.IsAhead).ShouldBe([false, false, false, true, true]);
        _viewModel.StepList.Select(s => s.Number).ShouldBe([1, 2, 3, 4, 5]);
    }

    public void Dispose() => _host.Dispose();

    private async Task SaveFirstAsync()
    {
        await _viewModel.StartCommand.ExecuteAsync(null);
        _viewModel.ProfileName = "Desk";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);
    }

    private async Task SaveBothAsync()
    {
        await SaveFirstAsync();
        _host.Display.SetSnapshot(RigActive());
        await _viewModel.RefreshDisplaysAsync();
        _viewModel.ProfileName = "Rig";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);
        _viewModel.Step.ShouldBe(SetupStep.Trigger);
    }
}
