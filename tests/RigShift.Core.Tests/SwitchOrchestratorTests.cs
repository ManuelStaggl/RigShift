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
    private readonly IUsbDeviceList _usbDevices = Substitute.For<IUsbDeviceList>();
    private readonly FakePowerController _power = new();
    private readonly FakeDuckingPreference _ducking = new();
    private readonly InMemoryDuckingMemory _duckingMemory = new();
    private readonly IWindowRescuer _windows = Substitute.For<IWindowRescuer>();
    private readonly ISwitchConfirmation _confirmation = Substitute.For<ISwitchConfirmation>();
    private readonly InMemorySwitchJournal _journal = new();
    private readonly FakeSurroundController _surround = new();

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
        var display = new FakeDisplayConfigurator([DeskActive(), RigActive()], applyResults: [87, 0]);

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
        result.Note.ShouldBe(SwitchNote.RestoredPrevious);
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
        // The budget ended it, not the cap: retries kept coming for the whole budget (analysis finding L-05).
        _time.Elapsed.ShouldBeInRange(options.TargetWaitBudget, options.TargetWaitBudget + options.PollInterval);
        result.Attempts.ShouldBeInRange((int)(options.TargetWaitBudget / options.PollInterval), options.MaxApplyAttempts - 1);
    }

    [Fact]
    public async Task Switch_DisplayWakesLate_ThenError31_HasFreshRetryBudget()
    {
        // The ultrawide sleeps for 19 of the 20 s, then answers 31 twice more: the retries need their own budget.
        var display = new FakeDisplayConfigurator(
            [.. Enumerable.Repeat(DeskActive(ultrawideAvailable: false), 19), DeskActive()], applyResults: [31, 31, 31, 31, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(5);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(21));
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
    public async Task Switch_RequiredDisplayNotAttached_AsksForItAndBlocksAfterTheWait()
    {
        var display = new FakeDisplayConfigurator(Snapshot(Attached(Desk4K, activeMode: DeskModes[0]), Attached(Tablet)));
        SwitchOrchestrator orchestrator = Create(display);
        IReadOnlyList<DisplayAssignment>? asked = null;
        orchestrator.WaitingForDisplays += (_, displays) => asked = displays;

        SwitchResult result = await orchestrator.SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Blocked);
        result.Message.ShouldNotBeNull().ShouldContain("Ultrawide 49");
        asked.ShouldNotBeNull().ShouldHaveSingleItem().Identity.ShouldBe(Ultrawide);
        display.Applied.ShouldBeEmpty();
        _time.Elapsed.ShouldBe(new SwitchOptions().MissingDisplayWaitBudget);
    }

    [Fact]
    public async Task Switch_RequiredDisplaySwitchedOnWhileWaiting_Applies()
    {
        // HW-16: the G9 had left the bus; the user switches it on after the notification.
        DisplaySnapshot ultrawideOff = Snapshot(Attached(Desk4K, activeMode: DeskModes[0]), Attached(Tablet));
        var display = new FakeDisplayConfigurator([ultrawideOff, ultrawideOff, ultrawideOff, DeskActive()]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.Applied.Single().Plan.Resolved.ShouldContain(r => r.Target.Identity == Ultrawide);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Switch_OptionalDisplayMissing_CountsAsApplied()
    {
        var display = new FakeDisplayConfigurator(DeskActive(tabletAttached: false));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        // HW-03: no "partially" for a spacedesk viewer that is not there; the plan still lists it for the catch-up.
        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Plan.ShouldRetryLater.ShouldBeTrue();
        display.Applied.Single().Plan.Resolved.Single().Target.Identity.ShouldBe(Ultrawide);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task CatchUp_OptionalDisplayAppears_ReappliesFullProfile()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult? result = await Create(display).CatchUpAsync(Rig(confirm: true), appliedDisplays: 1, Ct);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        display.Applied.Single().Plan.Resolved.Count.ShouldBe(2);
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
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

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), new SwitchRequest { DryRun = true }, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.DryRun);
        result.Plan.Resolved.Count.ShouldBe(2);
        display.Applied.ShouldBeEmpty();
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Switch_ConfirmationTimeout_RollsBackToPreviousTopology()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), TimeSpan.FromSeconds(15), Arg.Any<CancellationToken>())
            .Returns(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

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
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        display.Applied.Count.ShouldBe(4);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Switch_Rejected_RollsBack()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
    }

    [Fact]
    public async Task Switch_RollbackFails_ReportsFailed()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [0, 87, 87]);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.LastNativeError.ShouldBe(87);
        result.Message.ShouldNotBeNull().ShouldContain("restoring the previous topology failed");
        result.Note.ShouldBe(SwitchNote.RestoreFailed);
    }

    [Fact]
    public async Task Switch_TransientThenNonTransientInOneCycle_StillWaits()
    {
        var display = new FakeDisplayConfigurator(DeskActive(), applyResults: [31, 87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Attempts.ShouldBe(3);
        result.Note.ShouldBe(SwitchNote.None);
    }

    [Fact]
    public async Task Switch_DatabaseModesDiffer_ReportsIt()
    {
        var display = new FakeDisplayConfigurator([DeskActive(), RigActive(Mode(Ultrawide, 5120, 1440, 120, primary: true))], applyResults: [87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Note.ShouldBe(SwitchNote.ModesFromDatabase);
    }

    [Fact]
    public async Task Switch_DatabaseModesAsPlanned_HasNoNote()
    {
        var display = new FakeDisplayConfigurator([DeskActive(), RigActive()], applyResults: [87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Note.ShouldBe(SwitchNote.None);
    }

    [Fact]
    public async Task Switch_DatabaseModesLeaveDisplayDark_ReportsAppliedPartially()
    {
        DisplaySnapshot tabletDark = Snapshot(
            Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide, activeMode: UltrawideMode), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), tabletDark], applyResults: [87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.AppliedPartially);
        result.Note.ShouldBe(SwitchNote.ModesFromDatabase);
        result.Plan.Resolved.Single().Target.Identity.ShouldBe(Ultrawide);
        result.Plan.Missing.Single().Assignment.Identity.ShouldBe(Tablet);
    }

    [Fact]
    public async Task Switch_ApplyThrows_RestoresPreviousTopologyAndReportsFailed()
    {
        DisplaySnapshot allDark = Snapshot(Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), allDark]);
        display.ApplyExceptions.Enqueue(new System.ComponentModel.Win32Exception(31));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoredPrevious);
        display.Applied.Count.ShouldBe(2);
        display.Applied[1].Plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
    }

    [Fact]
    public async Task Switch_QueryThrowsWhileWaiting_RestoresPreviousTopology()
    {
        DisplaySnapshot allDark = Snapshot(Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), allDark], applyResults: [31, 31, 0]);
        display.QueryExceptions.Enqueue(null);
        display.QueryExceptions.Enqueue(new System.ComponentModel.Win32Exception(87));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoredPrevious);
    }

    [Fact]
    public async Task Switch_FailureAndNoPreviousDisplayAvailable_ReportsRestoreFailed()
    {
        var display = new FakeDisplayConfigurator([DeskActive(), Snapshot(Attached(Ultrawide), Attached(Tablet))], applyResults: [87, 87]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoreFailed);
        display.Applied.Count.ShouldBe(2);
    }

    [Fact]
    public async Task CatchUp_ApplyFails_LeavesDisplaysDark_RestoresPrevious()
    {
        DisplaySnapshot allDark = Snapshot(Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), allDark], applyResults: [87, 87, 0]);

        SwitchResult? result = await Create(display).CatchUpAsync(Rig(), appliedDisplays: 1, Ct);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoredPrevious);
        display.Applied.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Switch_CancelledDuringConfirmation_RollsBackBeforeThrowing()
    {
        using var exit = new CancellationTokenSource();
        var display = new FakeDisplayConfigurator(DeskActive());
        // Like the countdown window on app exit: the token fires and the window closes without an answer.
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(_ =>
        {
            exit.Cancel();
            return Task.FromResult(ConfirmationResult.Rejected);
        });

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Create(display).SwitchAsync(Rig(confirm: true) with { DisableCommunicationsDucking = true }, SwitchRequest.Default, exit.Token));

        display.Applied.Count.ShouldBe(2);
        display.Applied[1].Plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
        _ducking.Value.ShouldBe(CommunicationsDucking.ReduceBy50Percent);
        _duckingMemory.Remembered.ShouldBeNull();
    }

    /// <summary>
    /// Anything thrown between the apply and the answer used to leave the arrangement nobody confirmed – on displays
    /// the user may not be able to see.
    /// </summary>
    [Fact]
    public async Task Switch_ConfirmationThrows_RollsBackAndRestoresAudio()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.All)]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default!, default, default)
            .ThrowsAsyncForAnyArgs(new InvalidOperationException("the dialog could not be shown"));
        _power.SetKeepAwake(false);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones }) with { KeepAwake = true }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoredPrevious);
        result.Message.ShouldNotBeNull().ShouldContain("the dialog could not be shown");
        display.Applied.Count.ShouldBe(2);
        display.Applied[1].Plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
        await _audio.Received(1).SetDefaultAsync(Speakers, AudioRoleMask.All, Arg.Any<CancellationToken>());
        _power.IsKeepingAwake.ShouldBeFalse();
        _journal.Entry.ShouldBeNull();
    }

    /// <summary>App exit while the audio switches: no answer will come, so the old arrangement comes back first.</summary>
    [Fact]
    public async Task Switch_CancelledBetweenApplyAndConfirmation_RollsBackBeforeThrowing()
    {
        using var exit = new CancellationTokenSource();
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs<bool>(_ =>
        {
            exit.Cancel();
            throw new OperationCanceledException(exit.Token);
        });
        var display = new FakeDisplayConfigurator(DeskActive());

        await Should.ThrowAsync<OperationCanceledException>(() => Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, exit.Token));

        display.Applied.Count.ShouldBe(2);
        display.Applied[1].Plan.Resolved.Select(r => r.Target.Identity).ShouldBe([Desk4K, DeskLeft, DeskRight], ignoreOrder: true);
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
    }

    /// <summary>The rollback asks the driver first; when that throws, the audio still has to come back.</summary>
    [Fact]
    public async Task Switch_RollbackQueryThrows_StillRestoresAudioAndReportsFailed()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.All)]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());
        display.QueryExceptions.Enqueue(null);
        display.QueryExceptions.Enqueue(new InvalidOperationException("driver reset"));

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Note.ShouldBe(SwitchNote.RestoreFailed);
        await _audio.Received(1).SetDefaultAsync(Speakers, AudioRoleMask.All, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switch_Confirmed_KeepsNewTopology()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Confirmed);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.Applied.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData(false, false, 15)]
    [InlineData(true, true, 15)]
    [InlineData(true, false, 0)]
    public async Task Switch_SkipsConfirmation_WhenProfileOrRequestOrSettingSaysSo(bool confirm, bool skipRequested, int appSeconds)
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm), new SwitchRequest { SkipConfirmation = skipRequested, DefaultConfirmTimeoutSeconds = appSeconds }, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        await _confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task Switch_FromLink_TimeoutZero_StillConfirms()
    {
        // Analysis finding H-02: a web page must not switch without asking, even with confirmation turned off.
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: false),
            new SwitchRequest { FromLink = true, SkipConfirmation = true, DefaultConfirmTimeoutSeconds = 0 },
            Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        await _confirmation.Received(1).ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), SwitchOptions.DefaultConfirmTimeout, Arg.Any<CancellationToken>());
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
            .Returns([new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.All)]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        Received.InOrder(() =>
        {
            _audio.SetDefaultAsync(Headphones, AudioRoleMask.All, Arg.Any<CancellationToken>());
            _audio.SetDefaultAsync(Speakers, AudioRoleMask.All, Arg.Any<CancellationToken>());
        });
    }

    /// <summary>Speakers for sound, a headset for calls: a rollback used to hand the calls to the speakers as well.</summary>
    [Fact]
    public async Task Switch_Rollback_RestoresTheDefaultOfEachRole()
    {
        var headset = new AudioEndpoint("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000004}", "Headset");
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>()).Returns(
        [
            new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.Console | AudioRoleMask.Multimedia),
            new AudioDeviceInfo(headset, AudioDirection.Render, IsActive: true, AudioRoleMask.Communications),
            new AudioDeviceInfo(Headphones, AudioDirection.Render, IsActive: true, AudioRoleMask.None),
        ]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones }), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        await _audio.Received(1).SetDefaultAsync(Speakers, AudioRoleMask.Console | AudioRoleMask.Multimedia, Arg.Any<CancellationToken>());
        await _audio.Received(1).SetDefaultAsync(headset, AudioRoleMask.Communications, Arg.Any<CancellationToken>());
        await _audio.DidNotReceive().SetDefaultAsync(Speakers, AudioRoleMask.All, Arg.Any<CancellationToken>());
    }

    /// <summary>Only the way back may duplicate displays: it restores what Windows showed, a profile describes a desktop each.</summary>
    [Fact]
    public async Task Switch_Rollback_AllowsDuplicatedDisplays_TheSwitchItselfDoesNot()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        display.Applied.Select(a => a.Options.AllowClone).ShouldBe([false, true]);
    }

    /// <summary>Only the roles the switch touched come back – the call device the profile left alone stays alone.</summary>
    [Fact]
    public async Task Switch_Rollback_LeavesUntouchedRolesAlone()
    {
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Speakers, AudioDirection.Render, IsActive: true, AudioRoleMask.All)]);
        _audio.SetDefaultAsync(default!, default, default).ReturnsForAnyArgs(true);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { PlaybackCommunications = Headphones }), SwitchRequest.Default, Ct);

        await _audio.Received(1).SetDefaultAsync(Speakers, AudioRoleMask.Communications, Arg.Any<CancellationToken>());
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
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true, audio: new AudioAssignment { Playback = Headphones, PlaybackVolumePercent = 40 }), SwitchRequest.Default, Ct);

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
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Confirmed);
        _apps.IsRunning("C:\\Tools\\Discord.exe").Returns(true);
        _apps.StopAsync(default!, default, default).ReturnsForAnyArgs(true);
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig(confirm: true) with
        {
            Apps =
            [
                new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe", Arguments = "--minimized", WaitSeconds = 3 },
                new AppAction { Kind = AppActionKind.Stop, Path = "C:\\Tools\\Discord.exe" },
            ],
        };

        SwitchResult result = await Create(display).SwitchAsync(rig, SwitchRequest.Default, Ct);

        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
        Received.InOrder(() =>
        {
            _confirmation.ConfirmAsync(rig, Arg.Any<DisplaySnapshot>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
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

        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
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
        result.Apps.ShouldBe(AppsOutcome.Pending);
        (await result.AppsCompletion).ShouldBe(AppsOutcome.Incomplete);
        _apps.Received(1).Start("C:\\SimHub\\SimHubWPF.exe", null);
    }

    [Fact]
    public async Task Switch_NotConfirmed_RunsNoApps()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.TimedOut);
        var display = new FakeDisplayConfigurator(DeskActive());
        Profile rig = Rig(confirm: true) with { Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }] };

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
    public async Task Switch_NotConfirmed_RestoresKeepAwake()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        SwitchResult result = await orchestrator.SwitchAsync(Rig(confirm: true) with { KeepAwake = true }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _power.IsKeepingAwake.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_DryRunOrBlocked_LeavesKeepAwakeAlone()
    {
        Profile rig = Rig() with { KeepAwake = true };

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, new SwitchRequest { DryRun = true }, Ct);
        await Create(new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false))).SwitchAsync(rig, SwitchRequest.Default, Ct);

        _power.IsKeepingAwake.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_PowerFailure_DoesNotFailTheSwitch()
    {
        _power.Fail = true;

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(
            Rig() with { KeepAwake = true }, SwitchRequest.Default, Ct);

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
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        DisplaySnapshot deskHdrOn = Snapshot(Attached(Desk4K, activeMode: DeskModes[0] with { Hdr = true }), Attached(Ultrawide));
        DisplaySnapshot rigActive = Snapshot(Attached(Desk4K), Attached(Ultrawide, activeMode: UltrawideMode));
        DisplaySnapshot deskHdrOff = Snapshot(Attached(Desk4K, activeMode: DeskModes[0] with { Hdr = false }), Attached(Ultrawide));
        var display = new FakeDisplayConfigurator([deskHdrOn, rigActive, deskHdrOff]);

        SwitchResult result = await Create(display).SwitchAsync(
            Rig(confirm: true) with { Displays = [UltrawideMode] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        display.HdrSet.ShouldBe([(Desk4K.TargetDevicePath, true)]);
    }

    [Fact]
    public async Task Switch_HdrNotReportedRightAfterApply_AsksAgainAfterOneSecond()
    {
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = null })),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false })),
        ]);
        var options = new SwitchOptions { WindowRescueDelay = TimeSpan.Zero };

        SwitchResult result = await Create(display, options).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.ShouldBe([(Ultrawide.TargetDevicePath, true)]);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(1));
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

    [Fact]
    public async Task Switch_HdrFailsOnOneDisplay_OthersStillSet()
    {
        DisplayAssignment left = Mode(DeskLeft, 1920, 1080, 100, x: 5120);
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(DeskLeft, activeMode: left with { Hdr = false })),
        ]);
        display.HdrResults[Ultrawide.TargetDevicePath] = 87;

        SwitchResult result = await Create(display).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }, left with { Hdr = true }] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.ShouldBe([(Ultrawide.TargetDevicePath, true), (DeskLeft.TargetDevicePath, true)]);
    }

    [Fact]
    public async Task Switch_HdrCallHangs_SwitchGoesOnAndLeavesOtherDisplaysAlone()
    {
        // HW-12: the native HDR call never returned and nothing after it ran.
        DisplayAssignment left = Mode(DeskLeft, 1920, 1080, 100, x: 5120);
        var display = new FakeDisplayConfigurator([
            DeskActive(),
            Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(DeskLeft, activeMode: left with { Hdr = false })),
        ])
        {
            HdrNeverReturns = true,
        };

        SwitchResult result = await Create(display).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }, left with { Hdr = true }] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.ShouldBe([(Ultrawide.TargetDevicePath, true)]);
    }

    /// <summary>A driver frozen inside SetDisplayConfig used to freeze the switch with it – no result, no log line, no end.</summary>
    [Fact]
    public async Task Switch_ApplyCallHangs_FailsInsteadOfWaitingForever()
    {
        var display = new FakeDisplayConfigurator(DeskActive()) { ApplyNeverReturns = true };

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct)
            .WaitAsync(TimeSpan.FromSeconds(5), Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        result.Message.ShouldNotBeNull().ShouldContain("did not return");
        display.Applied.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Switch_DisplaysStillChangingAfterApply_HdrWaitsUntilTheySettle()
    {
        DisplaySnapshot tabletComing = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(Tablet));
        DisplaySnapshot tabletActive = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(Tablet, activeMode: TabletMode));
        var display = new FakeDisplayConfigurator([DeskActive(), tabletComing, tabletActive, tabletActive]);
        var options = new SwitchOptions { WindowRescueDelay = TimeSpan.Zero };

        SwitchResult result = await Create(display, options).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }, TabletMode] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.ShouldBe([(Ultrawide.TargetDevicePath, true)]);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task Switch_DisplaysNeverSettle_HdrLeftUnchanged()
    {
        DisplaySnapshot a = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(Tablet));
        DisplaySnapshot b = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false }), Attached(Tablet, activeMode: TabletMode));
        var display = new FakeDisplayConfigurator([DeskActive(), .. Enumerable.Range(0, 40).Select(i => i % 2 == 0 ? a : b)]);

        SwitchResult result = await Create(display).SwitchAsync(
            Rig() with { Displays = [UltrawideMode with { Hdr = true }, TabletMode] }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        display.HdrSet.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_Applied_RescuesWindowsAfterTheDelay()
    {
        var options = new SwitchOptions { WindowRescueDelay = TimeSpan.FromSeconds(1) };

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive()), options).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        _windows.Received(1).RescueOffscreenWindows();
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Switch_Applied_PutsTheDesktopSymbolsBack()
    {
        Profile rig = Rig() with { DesktopIcons = Layout };

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        DesktopIcons.Restores.ShouldBe(1);
        DesktopIcons.Capture().ShouldNotBeNull().Icons.ShouldBe(Layout.Icons);
    }

    [Fact]
    public async Task Switch_WithoutSavedSymbols_LeavesTheDesktopAlone()
    {
        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        DesktopIcons.Restores.ShouldBe(0);
    }

    [Fact]
    public async Task Switch_WhenExplorerMovesTheSymbolsAgain_PutsThemBackOnceMore()
    {
        // Explorer lays the desktop out itself a moment after the arrangement changed, which undoes the first attempt.
        DesktopIcons.MovesAfterRestore = 1;
        Profile rig = Rig() with { DesktopIcons = Layout };

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        DesktopIcons.Restores.ShouldBe(2);
    }

    [Fact]
    public async Task Switch_WhenWindowsArrangesTheSymbols_StopsAfterOneAttempt()
    {
        // "Auto arrange icons" overrides every position, so trying again only wastes time.
        DesktopIcons.Outcome = DesktopIconOutcome.AutoArrange;
        Profile rig = Rig() with { DesktopIcons = Layout };

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        DesktopIcons.Restores.ShouldBe(1);
    }

    [Fact]
    public async Task Switch_RolledBack_LeavesTheDesktopSymbolsAlone()
    {
        // Same rule as the windows: a switch the user rejects must not leave the desktop rearranged.
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        Profile rig = Rig(confirm: true) with { DesktopIcons = Layout };

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        DesktopIcons.Restores.ShouldBe(0);
    }

    [Fact]
    public async Task Switch_RolledBack_MovesNoWindows()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _windows.DidNotReceive().RescueOffscreenWindows();
    }

    [Fact]
    public async Task Switch_Confirmed_RescuesWindowsOnlyAfterTheConfirmation()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Confirmed);

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        Received.InOrder(() =>
        {
            _confirmation.ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            _windows.RescueOffscreenWindows();
        });
    }

    [Fact]
    public async Task Switch_FailureRestoresPreviousTopology_RescuesAfterTheRestore()
    {
        DisplaySnapshot allDark = Snapshot(Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight), Attached(Ultrawide), Attached(Tablet));
        var display = new FakeDisplayConfigurator([DeskActive(), allDark], applyResults: [87, 87, 0]);

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Failed);
        _windows.Received(1).RescueOffscreenWindows();
    }

    [Fact]
    public async Task CatchUp_RescuesWindows()
    {
        await Create(new FakeDisplayConfigurator(DeskActive())).CatchUpAsync(Rig(), appliedDisplays: 1, Ct);

        _windows.Received(1).RescueOffscreenWindows();
    }

    [Fact]
    public async Task Switch_RescueThrows_DoesNotChangeTheOutcome()
    {
        _windows.RescueOffscreenWindows().Throws(new InvalidOperationException("EnumWindows failed"));
        _apps.IsRunning(default!).ReturnsForAnyArgs(false);
        Profile rig = Rig() with { Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }] };

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
    }

    [Fact]
    public async Task Switch_DryRunOrBlocked_RescuesNothing()
    {
        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig(), new SwitchRequest { DryRun = true }, Ct);
        await Create(new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false))).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        _windows.DidNotReceive().RescueOffscreenWindows();
    }

    [Fact]
    public async Task Switch_AppsWaitForDevice_AlreadyPresent_StartsWithoutWaiting()
    {
        _usbDevices.PresentDeviceIds().Returns(Present(Wheelbase));

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(RigWaitingForWheelbase(), SwitchRequest.Default, Ct);

        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
        _apps.Received(1).Start("C:\\SimHub\\SimHubWPF.exe", null);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Switch_AppsWaitForDevice_AppearsLater_StartsOnceItIsThere()
    {
        _usbDevices.PresentDeviceIds().Returns(Present(), Present(), Present(), Present(Wheelbase));

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(RigWaitingForWheelbase(), SwitchRequest.Default, Ct);

        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
        _apps.Received(1).Start("C:\\SimHub\\SimHubWPF.exe", null);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public async Task Switch_AppsWaitForDevice_NeverAppears_WaitsFixedTimeIgnoringSavedValue(int savedSeconds)
    {
        // Analysis decision O-04: the wait is fixed; a value saved by 1.3 no longer changes it.
        _usbDevices.PresentDeviceIds().Returns(Present("VID_046D&PID_C547"));

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(
            RigWaitingForWheelbase() with { AppsWaitSeconds = savedSeconds }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        (await result.AppsCompletion).ShouldBe(AppsOutcome.DeviceMissing);
        _apps.Received(1).Start("C:\\SimHub\\SimHubWPF.exe", null);
        _time.Elapsed.ShouldBe(TimeSpan.FromSeconds(Profiles.Profile.AppsDeviceWaitSeconds));
    }

    [Fact]
    public async Task Switch_AppsWithoutWaitDevice_DoNotPoll()
    {
        Profile rig = RigWaitingForWheelbase() with { AppsWaitForUsbDeviceId = null };

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, SwitchRequest.Default, Ct);

        (await result.AppsCompletion).ShouldBe(AppsOutcome.Applied);
        _usbDevices.DidNotReceive().PresentDeviceIds();
    }

    [Fact]
    public async Task Switch_AppsWaitForDevice_NewSwitchCancelsWait()
    {
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _usbDevices.PresentDeviceIds().Returns(call =>
        {
            polling.TrySetResult();
            _ = release.Wait(TimeSpan.FromSeconds(30), Ct);
            return Present();
        });
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        SwitchResult first = await orchestrator.SwitchAsync(RigWaitingForWheelbase() with { AppsWaitSeconds = 300 }, SwitchRequest.Default, Ct);
        first.Apps.ShouldBe(AppsOutcome.Pending);
        await polling.Task;

        SwitchResult second = await orchestrator.SwitchAsync(Rig(), SwitchRequest.Default, Ct);
        release.Set();

        second.Outcome.ShouldBe(SwitchOutcome.Applied);
        (await first.AppsCompletion).ShouldBe(AppsOutcome.Cancelled);
        _apps.DidNotReceiveWithAnyArgs().Start(default!, default);
    }

    [Fact]
    public async Task Switch_AppsWaitForDevice_NotConfirmed_NeitherWaitsNorStartsApps()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(
            RigWaitingForWheelbase() with { SwitchWithoutAsking = false }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _usbDevices.DidNotReceive().PresentDeviceIds();
        _apps.DidNotReceiveWithAnyArgs().Start(default!, default);
        _time.Elapsed.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task Switch_Ducking_IsTurnedOffAndRestoredByProfileWithoutIt()
    {
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        await orchestrator.SwitchAsync(Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);
        _ducking.Value.ShouldBe(CommunicationsDucking.DoNothing);

        // A second profile with the flag keeps the value from before the first one.
        _ducking.Value = CommunicationsDucking.MuteOtherSounds;
        await orchestrator.SwitchAsync(Rig() with { Name = "VR", DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);
        _ducking.Value.ShouldBe(CommunicationsDucking.DoNothing);

        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _ducking.Value.ShouldBe(CommunicationsDucking.ReduceBy50Percent);

        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _ducking.Written.ShouldBe([CommunicationsDucking.DoNothing, CommunicationsDucking.DoNothing, CommunicationsDucking.ReduceBy50Percent]);
    }

    [Fact]
    public async Task Switch_Ducking_MissingValueComesBackAsMissing()
    {
        _ducking.Value = null;
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        await orchestrator.SwitchAsync(Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);
        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);

        _ducking.Written.ShouldBe([CommunicationsDucking.DoNothing, null]);
    }

    [Fact]
    public async Task Switch_ProfileWithoutDucking_LeavesTheUserValueAlone()
    {
        _ducking.Value = CommunicationsDucking.DoNothing;

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        _ducking.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_NotConfirmed_RestoresDucking()
    {
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        SwitchResult result = await orchestrator.SwitchAsync(
            Rig(confirm: true) with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _ducking.Value.ShouldBe(CommunicationsDucking.ReduceBy50Percent);
        _ducking.Written.ShouldBe([CommunicationsDucking.DoNothing, CommunicationsDucking.ReduceBy50Percent]);

        // Nothing is remembered from the rejected switch: a later profile without the flag changes nothing.
        _ducking.Value = CommunicationsDucking.MuteOtherSounds;
        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _ducking.Value.ShouldBe(CommunicationsDucking.MuteOtherSounds);
    }

    [Fact]
    public async Task Switch_DryRunOrBlocked_LeavesDuckingAlone()
    {
        Profile rig = Rig() with { DisableCommunicationsDucking = true };

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(rig, new SwitchRequest { DryRun = true }, Ct);
        await Create(new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false))).SwitchAsync(rig, SwitchRequest.Default, Ct);

        _ducking.Written.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_DuckingProfile_StoresMemory()
    {
        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);

        _duckingMemory.Remembered.ShouldBe(new RememberedDucking(CommunicationsDucking.ReduceBy50Percent));
    }

    [Fact]
    public async Task NewOrchestrator_WithMemory_RestoresOnProfileWithoutFlag()
    {
        // Rig with the flag, then the process ends (crash, restart, update) and a new orchestrator switches to the desk.
        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);

        await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);

        _ducking.Value.ShouldBe(CommunicationsDucking.ReduceBy50Percent);
        _duckingMemory.Remembered.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreDuckingIfUnused_ActiveProfileWithoutFlagOrNone_RestoresAndClears()
    {
        _duckingMemory.Remembered = new RememberedDucking(CommunicationsDucking.MuteOtherSounds);
        _ducking.Value = CommunicationsDucking.DoNothing;

        await Create(new FakeDisplayConfigurator(DeskActive())).RestoreDuckingIfUnusedAsync(activeProfile: null, Ct);

        _ducking.Value.ShouldBe(CommunicationsDucking.MuteOtherSounds);
        _duckingMemory.Remembered.ShouldBeNull();
    }

    [Fact]
    public async Task RestoreDuckingIfUnused_ActiveProfileWithFlag_KeepsMemoryUntilNextSwitch()
    {
        _duckingMemory.Remembered = new RememberedDucking(CommunicationsDucking.ReduceBy80Percent);
        _ducking.Value = CommunicationsDucking.DoNothing;
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));

        await orchestrator.RestoreDuckingIfUnusedAsync(Rig() with { DisableCommunicationsDucking = true }, Ct);
        _ducking.Written.ShouldBeEmpty();

        await orchestrator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default, Ct);
        _ducking.Value.ShouldBe(CommunicationsDucking.ReduceBy80Percent);
    }

    [Fact]
    public async Task Switch_Rejected_ClearsNothingButRestoresPreviousValue()
    {
        SwitchOrchestrator orchestrator = Create(new FakeDisplayConfigurator(DeskActive()));
        await orchestrator.SwitchAsync(Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await orchestrator.SwitchAsync(Rig(confirm: true) with { Name = "Desk" }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _ducking.Value.ShouldBe(CommunicationsDucking.DoNothing);
        _duckingMemory.Remembered.ShouldBe(new RememberedDucking(CommunicationsDucking.ReduceBy50Percent));
    }

    [Fact]
    public async Task Switch_DuckingFailure_DoesNotFailTheSwitch()
    {
        _ducking.Fail = true;

        SwitchResult result = await Create(new FakeDisplayConfigurator(DeskActive())).SwitchAsync(
            Rig() with { DisableCommunicationsDucking = true }, SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
    }

    private const string Wheelbase = "VID_0EB7&PID_0020";

    private static HashSet<string> Present(params string[] ids) => new(ids, StringComparer.OrdinalIgnoreCase);

    /// <summary>The rig as Windows reports it after the switch, optionally with another ultrawide mode.</summary>
    private static DisplaySnapshot RigActive(DisplayAssignment? ultrawideMode = null) => Snapshot(
        Attached(Desk4K), Attached(DeskLeft), Attached(DeskRight),
        Attached(Ultrawide, activeMode: ultrawideMode ?? UltrawideMode), Attached(Tablet, activeMode: TabletMode));

    private static Profile RigWaitingForWheelbase() => Rig() with
    {
        Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }],
        AppsWaitForUsbDeviceId = "vid_0eb7&pid_0020",
        AppsWaitForUsbDeviceName = "Wheelbase",
    };

    // The rescue delay is zero unless a test sets it, so waits measured elsewhere stay exact.
    /// <summary>
    /// The way back is recorded before the screens change and dropped once the switch is over – whatever it ended as.
    /// Only a process that dies in between leaves the record, and that is the case it exists for (plan point 22).
    /// </summary>
    [Fact]
    public async Task Switch_RecordsThePreviousTopologyAndDropsItWhenDone()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        InterruptedSwitch recorded = _journal.Written.ShouldHaveSingleItem();
        recorded.TargetProfileName.ShouldBe("Rig");
        recorded.Previous.Displays.ShouldNotBeEmpty();
        _journal.Entry.ShouldBeNull();
    }

    [Fact]
    public async Task Switch_NotConfirmed_DropsTheRecordAfterTheRollback()
    {
        var display = new FakeDisplayConfigurator(DeskActive());
        _confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(ConfirmationResult.Rejected);

        SwitchResult result = await Create(display).SwitchAsync(Rig(confirm: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _journal.Written.ShouldHaveSingleItem();
        _journal.Entry.ShouldBeNull();
    }

    /// <summary>A blocked switch never touches the screens, so it must not leave a record asking to undo anything.</summary>
    [Fact]
    public async Task Switch_Blocked_RecordsNothing()
    {
        var display = new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false));

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Blocked);
        display.Applied.ShouldBeEmpty();
        _journal.Written.ShouldBeEmpty();
    }

    /// <summary>The desktop the switch tidies up; tests that care about it reach in here.</summary>
    private FakeDesktopIcons DesktopIcons { get; } = new();

    /// <summary>A desktop layout to hand a profile: one shortcut and the recycle bin.</summary>
    private static readonly DesktopIconLayout Layout = new()
    {
        Icons =
        [
            new DesktopIcon { Item = @"C:\Users\sim\Desktop\SimHub.lnk", X = 20, Y = 20 },
            new DesktopIcon { Item = "::{645FF040-5081-101B-9F08-00AA002F954E}", X = 20, Y = 140 },
        ],
        CapturedAt = DateTimeOffset.UnixEpoch,
    };

    private SwitchOrchestrator Create(FakeDisplayConfigurator display, SwitchOptions? options = null) =>
        new(display, _audio, _apps, _usbDevices, _power, _ducking, _duckingMemory, _windows, DesktopIcons, _surround, _confirmation, _journal, new TopologyPlanner(new TopologyPlannerOptions()),
            options ?? new SwitchOptions { WindowRescueDelay = TimeSpan.Zero }, _time, Logger.None);
}
