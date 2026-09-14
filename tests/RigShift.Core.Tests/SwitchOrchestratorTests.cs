using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

// NSubstitute arrange/assert calls take a CancellationToken argument matcher, not a token to observe.
#pragma warning disable xUnit1051

public sealed class SwitchOrchestratorTests
{
    private static readonly AudioEndpoint Headphones = new("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}", "Headphones");
    private static readonly AudioEndpoint Speakers = new("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000002}", "Speakers");
    private static readonly AudioEndpoint Microphone = new("{0.0.1.00000000}.{00000000-0000-0000-0000-000000000003}", "Microphone");

    private readonly AutoAdvanceTimeProvider _time = new();
    private readonly IAudioController _audio = Substitute.For<IAudioController>();
    private readonly IAppLauncher _apps = Substitute.For<IAppLauncher>();
    private readonly FakePowerController _power = new();
    private readonly ISwitchConfirmation _confirmation = Substitute.For<ISwitchConfirmation>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Switch_AppliesWithStoredModes_InOneCall()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(1);
        display.Applied.Single().Options.UseDatabaseModes.ShouldBeFalse();
        display.Applied.Single().Plan.Resolved.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Switch_StoredModesFail_FallsBackToDatabaseModes()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(2);
        display.Applied.Select(a => a.Options.UseDatabaseModes).ShouldBe([false, true]);
    }

    [Fact]
    public async Task Switch_NonTransientError_FailsWithoutWaiting()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [87, 87]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Attempts.ShouldBe(2);
        result.LastNativeError.ShouldBe(87);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Switch_Error31_WaitsRequeriesAndSucceeds()
    {
        // The 2026-09-13 log case: stored modes and database modes both fail with 31, later the call works.
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [31, 31, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(3);
        result.LastNativeError.ShouldBe(31);
        display.QueryCount.ShouldBe(2);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(1));
        display.Applied[2].Options.UseDatabaseModes.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_UltrawideDropsOffMidSwitch_WaitsForItAndSucceeds()
    {
        // M5 log 2026-09-13 19:52: 31, then 1610 while the sleeping G9 vanished from the bus; it reappeared 3 s later.
        DisplaySnapshot ultrawideGone = Snapshot(
            Attached(Desk4K, activeMode: DeskModes[0]),
            Attached(DeskLeft, activeMode: DeskModes[1]),
            Attached(DeskRight, activeMode: DeskModes[2]),
            Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), ultrawideGone, ultrawideGone, DeskActive()], applyResults: [31, 1610, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(3);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Switch_FailureLeavesDeskDark_RestoresPreviousTopology()
    {
        DisplaySnapshot allDark = Snapshot(Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), allDark], applyResults: [87, 87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Message.ShouldNotBeNull().ShouldContain("Previous topology restored");
        display.Applied.Count.ShouldBe(3);
        display.Applied[2].Plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
    }

    [Fact]
    public async Task Switch_Error31Persists_FailsWhenTimeBudgetIsSpent()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: Enumerable.Repeat(31, 100));
        var options = new SwitchOptions { MaxApplyAttempts = 100 };

        SwitchResult result = await Create(display, options).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.LastNativeError.ShouldBe(31);
        _time.Elapsed.ShouldBe(options.TargetWaitBudget);
        result.Attempts.ShouldBe(42);
    }

    [Fact]
    public async Task Switch_Error31Persists_StopsAtAttemptCap()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: Enumerable.Repeat(31, 100));

        SwitchResult result = await Create(display, new SwitchOptions { MaxApplyAttempts = 5 }).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Attempts.ShouldBe(5);
    }

    [Fact]
    public async Task Switch_SleepingRequiredDisplay_WaitsUntilAvailable_ThenApplies()
    {
        var display = new FakeDisplayConfigurator(
            [DeskActive(ultrawideAvailable: false), DeskActive(ultrawideAvailable: false), DeskActive(ultrawideAvailable: false), DeskActive()]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(1);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
        display.Applied.Single().Plan.Resolved.ShouldContain(r => r.Target.Identity == Ultrawide && r.Target.IsAvailable);
    }

    [Fact]
    public async Task Switch_RequiredDisplayNeverWakes_IsBlockedAfterBudget_WithoutApplying()
    {
        var display = new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Blocked);
        result.Message.ShouldNotBeNull().ShouldContain("AttachedButUnavailable");
        display.Applied.ShouldBeEmpty();
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Switch_RequiredDisplayNotAttached_IsBlockedImmediately()
    {
        var display = new FakeDisplayConfigurator(Snapshot(Attached(Desk4K, activeMode: DeskModes[0]), Attached(Tablet)));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Blocked);
        result.Message.ShouldNotBeNull().ShouldContain("Ultrawide 49");
        display.Applied.ShouldBeEmpty();
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Switch_OptionalDisplayMissing_AppliesPartially()
    {
        var display = new FakeDisplayConfigurator(DeskActive(tabletAttached: false));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.AppliedPartially);
        display.Applied.Single().Plan.Resolved.Single().Target.Identity.ShouldBe(Ultrawide);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task CatchUp_OptionalDisplayAppears_ReappliesFullProfile()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult? result = await Create(display).CatchUpAsync(Rig(confirmSeconds: 15), appliedDisplays: 1, Ct);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        display.Applied.Single().Plan.Resolved.Count.ShouldBe(2);
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default, default);
    }

    [Fact]
    public async Task CatchUp_NothingNew_DoesNotApply()
    {
        var display = new FakeDisplayConfigurator(DeskActive(tabletAttached: false));

        SwitchResult? result = await Create(display).CatchUpAsync(Rig(), appliedDisplays: 1, Ct);

        result.ShouldBeNull();
        display.Applied.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_DryRun_TouchesNothing()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), new SwitchRequest { DryRun = true }, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.DryRun);
        result.Plan.Resolved.Count.ShouldBe(2);
        display.Applied.ShouldBeEmpty();
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default, default);
    }

    [Fact]
    public async Task Switch_ConfirmationTimeout_RollsBackToPreviousTopology()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(Arg.Any<Profile>(), TimeSpan.FromSeconds(15), Arg.Any<CancellationToken>())
            .Returns(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        result.Attempts.ShouldBe(2);
        display.Applied.Count.ShouldBe(2);
        TopologyPlan rollback = display.Applied[1].Plan;
        rollback.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
        rollback.Resolved.Single(r => r.Assignment.IsPrimary).Target.Identity.ShouldBe(Desk4K);
        rollback.Resolved.Single(r => r.Target.Identity == DeskLeft).Assignment.PositionX.ShouldBe(-1920);
    }

    [Fact]
    public async Task Switch_RollbackTargetsVanishMidRetry_WaitsForThemAndRestores()
    {
        // M5 log 2026-09-13 19:59: the rollback display dropped off the bus after 31/1610 and came back later.
        DisplaySnapshot deskGone = Snapshot(Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), DeskActive(), deskGone, DeskActive()], applyResults: [0, 31, 1610, 0]);
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        display.Applied.Count.ShouldBe(4);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Switch_Rejected_RollsBack()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
    }

    [Fact]
    public async Task Switch_RollbackFails_ReportsFailed()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [0, 87, 87]);
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.LastNativeError.ShouldBe(87);
        result.Message.ShouldNotBeNull().ShouldContain("restoring the previous topology failed");
    }

    [Fact]
    public async Task Switch_Confirmed_KeepsNewTopology()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Confirmed);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirmSeconds: 15), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.Applied.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(15, true)]
    public async Task Switch_SkipsConfirmation_WhenTimeoutIsZeroOrRequested(int confirmSeconds, bool skipRequested)
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirmSeconds), new SwitchRequest { SkipConfirmation = skipRequested }, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default, default);
    }

    [Fact]
    public async Task Switch_SetsPlaybackForAllRoles()
    {
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Audio.ShouldBe(AudioOutcome.Applied);
        await _audio.Received(1).SetDefaultAsync(Headphones, AudioRoleMask.All, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switch_AudioFailure_DoesNotFailDisplaySwitch()
    {
        _audio.SetDefaultAsync(default!, default, default).ThrowsAsyncForAnyArgs(new InvalidOperationException("COM error"));
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Audio.ShouldBe(AudioOutcome.Incomplete);
    }

    [Fact]
    public async Task Switch_Rollback_RestoresPreviousPlaybackDevice()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, IsDefault: true)]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirmSeconds: 15, audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        Received.InOrder(() =>
        {
            _audio.SetDefaultAsync(Headphones, AudioRoleMask.All, Arg.Any<CancellationToken>());
            _audio.SetDefaultAsync(Speakers, AudioRoleMask.All, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Switch_SetsPlaybackAndRecordingVolume()
    {
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(audio: new AudioAssignment
        {
            Playback = Headphones,
            PlaybackVolumePercent = 40,
            Recording = Microphone,
            RecordingVolumePercent = 80,
        }), SwitchRequest.Default, Ct);

        result.Audio.ShouldBe(AudioOutcome.Applied);
        await _audio.Received(1).SetVolumeAsync(Headphones, 40, Arg.Any<CancellationToken>());
        await _audio.Received(1).SetVolumeAsync(Microphone, 80, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switch_VolumeWithoutDevice_IsIgnored()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        await Create(display).SwitchAsync(Rig(audio: new AudioAssignment { RecordingVolumePercent = 80 }), SwitchRequest.Default, Ct);

        await _audio.DidNotReceiveWithAnyArgs().SetVolumeAsync(default!, default, default);
    }

    [Fact]
    public async Task Switch_Rollback_RestoresPreviousVolume()
    {
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _audio.GetVolumeAsync(Headphones, Arg.Any<CancellationToken>()).Returns(65);
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirmSeconds: 15, audio: new AudioAssignment { Playback = Headphones, PlaybackVolumePercent = 40 }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        Received.InOrder(() =>
        {
            _audio.SetVolumeAsync(Headphones, 40, Arg.Any<CancellationToken>());
            _audio.SetVolumeAsync(Headphones, 65, Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Switch_RunsAppsInOrder_AfterConfirmation_AndWaits()
    {
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Confirmed);
        _apps.IsRunning("C:\\Tools\\Discord.exe").Returns(true);
        _apps.StopAsync(default!, default, default).ReturnsForAnyArgs(true);
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig(confirmSeconds: 15) with
        {
            Apps =
            [
                new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe", Arguments = "--minimized", WaitSeconds = 3 },
                new AppAction { Kind = AppActionKind.Stop, Path = "C:\\Tools\\Discord.exe" },
            ],
        };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Apps.ShouldBe(AppsOutcome.Applied);
        Received.InOrder(() =>
        {
            _confirmation.ConfirmAsync(rig, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            _apps.Start("C:\\SimHub\\SimHubWPF.exe", "--minimized");
            _apps.StopAsync("C:\\Tools\\Discord.exe", TimeSpan.FromSeconds(5), Arg.Any<CancellationToken>());
        });
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Switch_DoesNotStartRunningApp_OrStopMissingOne()
    {
        _apps.IsRunning("C:\\SimHub\\SimHubWPF.exe").Returns(true);
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig() with
        {
            Apps =
            [
                new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe", WaitSeconds = 10 },
                new AppAction { Kind = AppActionKind.Stop, Path = "C:\\Tools\\Discord.exe" },
            ],
        };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Apps.ShouldBe(AppsOutcome.Applied);
        _apps.DidNotReceiveWithAnyArgs().Start(default!, default);
        await _apps.DidNotReceiveWithAnyArgs().StopAsync(default!, default, default);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Switch_AppFailure_ContinuesAndReportsIncomplete()
    {
        _apps.When(a => a.Start("C:\\Missing\\Tool.exe", Arg.Any<string?>())).Throw(new InvalidOperationException("not found"));
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig() with
        {
            Apps = [new AppAction { Path = "C:\\Missing\\Tool.exe" }, new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }],
        };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Apps.ShouldBe(AppsOutcome.Incomplete);
        _apps.Received(1).Start("C:\\SimHub\\SimHubWPF.exe", null);
    }

    [Fact]
    public async Task Switch_NotConfirmed_RunsNoApps()
    {
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig(confirmSeconds: 15) with { Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }] };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        result.Apps.ShouldBe(AppsOutcome.NotConfigured);
        _apps.DidNotReceiveWithAnyArgs().Start(default!, default);
    }

    [Fact]
    public async Task Switch_KeepAwake_FollowsTheProfile()
    {
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        await orchestrator.SwitchAsync(Rig() with { KeepAwake = true }, SwitchRequest.Default, Ct);
        _power.IsKeepingAwake.ShouldBeTrue();

        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _power.IsKeepingAwake.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_PowerPlan_IsSetAndRestoredByProfileWithoutPlan()
    {
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));
        var highPerformance = new PowerPlan(FakePowerController.HighPerformance, "High performance");
        var ultimate = new PowerPlan(FakePowerController.Ultimate, "Ultimate");

        await orchestrator.SwitchAsync(Rig() with { PowerPlan = highPerformance }, SwitchRequest.Default, Ct);
        _power.ActivePlan.ShouldBe(FakePowerController.HighPerformance);

        await orchestrator.SwitchAsync(Rig() with { Name = "VR", PowerPlan = ultimate }, SwitchRequest.Default, Ct);
        _power.ActivePlan.ShouldBe(FakePowerController.Ultimate);

        // The plan from before the first profile comes back, not the one in between.
        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _power.ActivePlan.ShouldBe(FakePowerController.Balanced);

        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _power.PlansSet.ShouldBe([FakePowerController.HighPerformance, FakePowerController.Ultimate, FakePowerController.Balanced]);
    }

    [Fact]
    public async Task Switch_ProfileWithoutPlan_LeavesAUserChosenPlanAlone()
    {
        _power.ActivePlan = FakePowerController.HighPerformance;

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        _power.PlansSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_NotConfirmed_RestoresKeepAwakeAndPlan()
    {
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));
        Profile rig = Rig(confirmSeconds: 15) with
        {
            KeepAwake = true,
            PowerPlan = new PowerPlan(FakePowerController.HighPerformance, "High performance"),
        };

        SwitchResult result = await orchestrator.SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _power.IsKeepingAwake.ShouldBeFalse();
        _power.ActivePlan.ShouldBe(FakePowerController.Balanced);
        _power.PlansSet.ShouldBe([FakePowerController.HighPerformance, FakePowerController.Balanced]);

        // Nothing is remembered from the rejected switch: a later profile without a plan changes nothing.
        _power.ActivePlan = FakePowerController.Ultimate;
        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _power.ActivePlan.ShouldBe(FakePowerController.Ultimate);
    }

    [Fact]
    public async Task Switch_DryRunOrBlocked_LeavesPowerAlone()
    {
        Profile rig = Rig() with { KeepAwake = true, PowerPlan = new PowerPlan(FakePowerController.HighPerformance, "High performance") };

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, new SwitchRequest { DryRun = true }, Ct);
        await Create(new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false))).SwitchAsync(rig, SwitchRequest.Default, Ct);

        _power.IsKeepingAwake.ShouldBeFalse();
        _power.PlansSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_PowerFailure_DoesNotFailTheSwitch()
    {
        _power.Fail = true;

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(
            Rig() with { KeepAwake = true, PowerPlan = new PowerPlan(FakePowerController.HighPerformance, "High performance") }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
    }

    [Fact]
    public async Task Switch_Hdr_IsSetWhereTheActiveDisplayDiffers()
    {
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(Tablet, activeMode: TabletMode)),
        ]);
        Profile rig = Rig() with { Displays = [UltrawideMode with { Hdr = true }, TabletMode with { Hdr = false }] };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        // The tablet reports no HDR support (null) and stays untouched.
        display.HdrSet.ShouldBe([(Ultrawide.TargetDevicePath, true)]);
    }

    [Fact]
    public async Task Switch_HdrUnchangedOrAlreadyRight_SetsNothing()
    {
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = true })),
        ]);

        await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);
        await Create(display).SwitchAsync(Rig() with { Displays = [UltrawideMode with { Hdr = true }, TabletMode] }, SwitchRequest.Default, Ct);

        display.HdrSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_NotConfirmed_RestoresHdr()
    {
        _confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        DisplaySnapshot deskHdrOn = Snapshot(Attached(Desk4K, activeMode: DeskModes[0] with { Hdr = true }), Attached(Ultrawide));
        DisplaySnapshot rigActive = Snapshot(Attached(Desk4K), Attached(Ultrawide, activeMode: UltrawideMode));
        DisplaySnapshot deskHdrOff = Snapshot(Attached(Desk4K, activeMode: DeskModes[0] with { Hdr = false }), Attached(Ultrawide));
        var display = new FakeDisplayConfigurator([deskHdrOn, rigActive, deskHdrOff]);

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirmSeconds: 15) with { Displays = [UltrawideMode] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        display.HdrSet.ShouldBe([(Desk4K.TargetDevicePath, true)]);
    }

    [Fact]
    public async Task Switch_HdrFailure_DoesNotFailTheSwitch()
    {
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false })),
        ])
        {
            HdrResult = 87,
        };

        SwitchResult result = await Create(display).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.Count.ShouldBe(1);
    }

    private SwitchOrchestrator Create(FakeDisplayConfigurator display, SwitchOptions? options = null) =>
        new(display, _audio, _apps, _power, _confirmation, new TopologyPlanner(new TopologyPlannerOptions()), options ?? new SwitchOptions(), _time, Logger.None);
}
