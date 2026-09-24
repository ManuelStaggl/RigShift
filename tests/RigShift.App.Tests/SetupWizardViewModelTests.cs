using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
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
    private static readonly Hotkey CtrlAltF1 = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 };
    private static readonly Hotkey CtrlAltF2 = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x71 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };

    private readonly IAudioController _audio = Substitute.For<IAudioController>();
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly SetupWizardViewModel _viewModel;

    public SetupWizardViewModelTests()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>()).Returns(
        [
            new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.All),
            new AudioDeviceInfo(Headset, AudioDirection.Render, IsActive: true, AudioRoleMask.None),
        ]);
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Dongle, "Dongle")]);
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());

        var games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        var sessions = new GameSessionService(
            games,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        var hotkeys = new HotkeyService(_host.Catalog, games, sessions, _host.Coordinator, _host.Settings, Logger.None, _registrar);
        _viewModel = new SetupWizardViewModel(
            _host.Catalog, _host.Display, _audio, _host.Usb, powerCheck,
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), _host.Settings, _host.Surround, hotkeys, Logger.None);
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

    [Fact]
    public async Task FirstStep_MakesItsProfileTheDefault()
    {
        await SaveFirstAsync();

        _host.Settings.Current.DefaultProfileId.ShouldBe(_viewModel.FirstProfile.ShouldNotBeNull().Id);
    }

    [Fact]
    public async Task FirstStep_WithADefaultProfileAlready_LeavesItAlone()
    {
        Profile existing = new() { Id = Guid.NewGuid(), Name = "Kept", Displays = [] };
        await _host.Catalog.SaveAsync(existing, CancellationToken.None);
        await _host.Catalog.ToggleDefaultAsync(existing, CancellationToken.None);

        await SaveFirstAsync();

        _host.Settings.Current.DefaultProfileId.ShouldBe(existing.Id);
    }

    [Fact]
    public async Task DoneStep_ShowsBothProfilesWithTheirState()
    {
        await SaveBothAsync();
        _viewModel.SkipTriggerCommand.Execute(null);

        _viewModel.Summary.Count.ShouldBe(2);
        _viewModel.Summary[0].IsDefault.ShouldBeTrue();
        _viewModel.Summary[0].HasStatus.ShouldBeTrue();
        // The second profile is the arrangement that is up right now, so it is the active one.
        _viewModel.Summary[1].IsActive.ShouldBeTrue();
        _viewModel.Summary[1].IsDefault.ShouldBeFalse();
    }

    /// <summary>The assistant ends with hotkeys the user can press right away, and says so (finding U-02).</summary>
    [Fact]
    public async Task SavedProfiles_GetCtrlAltF1AndF2_AndTheLastStepPointsToThem()
    {
        await SaveBothAsync();
        _viewModel.SkipTriggerCommand.Execute(null);

        _host.Catalog.Profiles.Single(p => p.Name == "Desk").Hotkey.ShouldBe(CtrlAltF1);
        _host.Catalog.Profiles.Single(p => p.Name == "Rig").Hotkey.ShouldBe(CtrlAltF2);
        _viewModel.HasHotkeys.ShouldBeTrue();
        _viewModel.StepText.ShouldBe(Loc.Instance["Setup_DoneNoRule"]);
    }

    [Fact]
    public async Task SuggestedHotkeyHeldByAnotherApplication_IsLeftOut()
    {
        _registrar.TakenElsewhere.Add(CtrlAltF1);
        _registrar.TakenElsewhere.Add(CtrlAltF2);

        await SaveBothAsync();
        _viewModel.SkipTriggerCommand.Execute(null);

        _host.Catalog.Profiles.ShouldAllBe(p => p.Hotkey == null);
        _viewModel.StepText.ShouldBe(Loc.Instance["Setup_DoneNoRuleNoHotkey"]);
    }

    [Fact]
    public async Task Back_ToAStep_KeepsTheHotkeyItsProfileAlreadyHas()
    {
        await SaveFirstAsync();
        Profile desk = _host.Catalog.Profiles.Single();
        await _host.Catalog.SaveAsync(desk with { Hotkey = CtrlAltD }, Ct);

        await _viewModel.BackCommand.ExecuteAsync(null);
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);

        _host.Catalog.Profiles.Single().Hotkey.ShouldBe(CtrlAltD);
    }

    /// <summary>"Try it" is the first real switch – back to the desk after the rig was set up (finding U-02).</summary>
    [Fact]
    public async Task TryIt_ClosesAndSwitchesBackToTheFirstProfile_WithStartWithWindowsOn()
    {
        await SaveBothAsync();
        await _host.Catalog.RefreshActiveAsync(Ct);
        _viewModel.SkipTriggerCommand.Execute(null);
        bool closed = false;
        _viewModel.CloseRequested += (_, _) => closed = true;

        _viewModel.StartWithWindows.ShouldBeTrue("the last step presets it (finding U-01)");
        _viewModel.ShowTry.ShouldBeTrue();
        _viewModel.TryText.ShouldBe(Loc.Format("Setup_TryIt", "Desk"));
        _viewModel.TryItCommand.Execute(null);

        closed.ShouldBeTrue();
        _viewModel.SwitchAfterClose.ShouldNotBeNull().Name.ShouldBe("Desk");
        _host.Settings.Autostart.Received(1).SetEnabled(true);
    }

    [Fact]
    public async Task Finish_WithStartWithWindowsTurnedOff_LeavesItOff()
    {
        await SaveBothAsync();
        _viewModel.SkipTriggerCommand.Execute(null);

        _viewModel.StartWithWindows = false;
        _viewModel.FinishCommand.Execute(null);

        _viewModel.SwitchAfterClose.ShouldBeNull();
        _host.Settings.Autostart.DidNotReceive().SetEnabled(Arg.Any<bool>());
    }

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// The triple rig saved while Surround runs keeps its grid, as "From the current arrangement" does; before, the
    /// assistant saved none and the first real switch blocked (finding U-05). The desk needs no setting of its own.
    /// </summary>
    [Fact]
    public async Task SecondStep_WhileSurroundRuns_SavesTheGrid()
    {
        await SaveFirstAsync();
        _viewModel.SurroundHint.ShouldBe(Loc.Instance["Setup_SurroundHint"]);

        var grid = new SurroundGrid
        {
            Rows = 1,
            Columns = 3,
            Width = 2560,
            Height = 1440,
            BezelCorrected = true,
            Displays = [new SurroundDisplay { DisplayId = 1, OverlapX = -64 }, new SurroundDisplay { DisplayId = 2, OverlapX = -64 }, new SurroundDisplay { DisplayId = 3 }],
        };
        _host.Surround.ActiveGrid = grid;
        _host.Display.SetSnapshot(RigActive());
        await _viewModel.RefreshDisplaysAsync();
        _viewModel.SurroundHint.ShouldBe(Loc.Instance["Setup_SurroundOn"]);
        _viewModel.ProfileName = "Rig";
        await _viewModel.SaveProfileCommand.ExecuteAsync(null);

        _host.Catalog.Profiles.Single(p => p.Name == "Rig").Surround.ShouldNotBeNull().Grid.ShouldBe(grid);
        _host.Catalog.Profiles.Single(p => p.Name == "Desk").Surround.ShouldBeNull();
        _viewModel.HasSurroundHint.ShouldBeFalse();
    }

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
