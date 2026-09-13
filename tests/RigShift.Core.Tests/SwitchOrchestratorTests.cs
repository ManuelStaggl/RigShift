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

    private readonly AutoAdvanceTimeProvider _time = new();
    private readonly IAudioController _audio = Substitute.For<IAudioController>();
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

    private SwitchOrchestrator Create(FakeDisplayConfigurator display, SwitchOptions? options = null) =>
        new(display, _audio, _confirmation, new TopologyPlanner(new TopologyPlannerOptions()), options ?? new SwitchOptions(), _time, Logger.None);
}
