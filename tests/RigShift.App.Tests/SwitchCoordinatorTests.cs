using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

// NSubstitute arrange calls take a CancellationToken argument matcher, not a token to observe.
#pragma warning disable xUnit1051

public sealed class SwitchCoordinatorTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    [Fact]
    public async Task Switch_WhileAnotherRuns_ReturnsNullAndRaisesBusyRejected()
    {
        var answer = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(answer.Task);
        bool busy = false;
        _host.Coordinator.BusyRejected += (_, _) => busy = true;

        Task<SwitchResult?> first = _host.Coordinator.SwitchAsync(Rig(confirm: true), SwitchRequest.Default);
        SwitchResult? second = await _host.Coordinator.SwitchAsync(Rig() with { Name = "Desk" }, SwitchRequest.Default);

        second.ShouldBeNull();
        busy.ShouldBeTrue();
        answer.SetResult(ConfirmationResult.Confirmed);
        (await first).ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        _host.Coordinator.IsSwitching.ShouldBeFalse();
    }

    [Fact]
    public async Task Switch_WhenAListenerThrows_StillFreesTheGateForTheNextSwitch()
    {
        // The tray rebuilt its icon on IsSwitching and threw when a game session raised it from a background thread.
        // That left the gate taken, and every later switch was refused as "another switch is running" until restart.
        bool thrown = false;
        _host.Coordinator.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SwitchCoordinator.IsSwitching) && !thrown)
            {
                thrown = true;
                throw new InvalidOperationException("the calling thread cannot access this object");
            }
        };

        // The game session takes this path (IProfileSwitcher), which passes the exception on to its caller.
        IProfileSwitcher switcher = _host.Coordinator;
        await Should.ThrowAsync<InvalidOperationException>(() => switcher.SwitchAsync(Rig(), SwitchRequest.Default, CancellationToken.None));

        _host.Coordinator.IsSwitching.ShouldBeFalse();
        SwitchResult? second = await _host.Coordinator.SwitchAsync(Rig(), SwitchRequest.Default);
        second.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
    }

    [Fact]
    public async Task Blocked_AsksForTheDisplay_AndNamesOnlyRequiredDisplays()
    {
        // HW-14 and HW-16: neither the ultrawide (required) nor the tablet (optional) is connected.
        _host.Display.SetSnapshot(Snapshot(Attached(Desk4K, activeMode: DeskModes[0])));
        IReadOnlyList<string>? asked = null;
        _host.Coordinator.WaitingForDisplays += (_, names) => asked = names;

        SwitchResult? result = await _host.Coordinator.SwitchAsync(Rig(), SwitchRequest.Default);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Blocked);
        asked.ShouldNotBeNull().ShouldHaveSingleItem().ShouldContain("Ultrawide 49");
        _host.Coordinator.History[0].MissingDisplays.ShouldHaveSingleItem().ShouldContain("Ultrawide 49");
    }

    [Fact]
    public async Task Switch_ThatStays_WritesAMonitorOnANewPortIntoEveryProfile()
    {
        // K-03: the ultrawide moved to another port and is found by its EDID. Afterwards every profile with it and its
        // custom name know the new path, so the next switch finds it directly.
        DisplayIdentity moved = Ultrawide with { TargetDevicePath = @"\\?\DISPLAY#SAM0001#OTHERPORT&9", EdidSerialHash = "0123456789ABCDEF" };
        Profile rig = Rig();
        Profile wide = Profile("Wide only", [UltrawideMode]);
        await _host.Store.SaveAsync(rig, CancellationToken.None);
        await _host.Store.SaveAsync(wide, CancellationToken.None);
        await _host.Settings.UpdateAsync(
            s => s with { DisplayNames = new Dictionary<string, string> { [Ultrawide.TargetDevicePath] = "Big one" } }, CancellationToken.None);
        await _host.Catalog.ReloadAsync(CancellationToken.None);
        _host.Display.SetSnapshot(Snapshot(Attached(Desk4K, activeMode: DeskModes[0]), Attached(moved), Attached(Tablet)));

        SwitchResult? result = await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        _host.Catalog.Profiles.Select(p => p.Displays[0].Identity).ShouldAllBe(identity => identity == moved);
        _host.Settings.Current.DisplayNames.ShouldNotBeNull()[moved.TargetDevicePath].ShouldBe("Big one");
    }

    [Fact]
    public async Task Blocked_ByIdenticalDisplaysOnNewPorts_SaysSoInsteadOfAskingToSwitchThemOn()
    {
        DisplayIdentity leftMoved = DeskLeft with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&1" };
        DisplayIdentity rightMoved = DeskRight with { TargetDevicePath = @"\\?\DISPLAY#DEL0003#NEW&2" };
        _host.Display.SetSnapshot(Snapshot(Attached(Desk4K, activeMode: DeskModes[0]), Attached(leftMoved), Attached(rightMoved), Attached(Ultrawide)));
        bool asked = false;
        _host.Coordinator.WaitingForDisplays += (_, _) => asked = true;

        SwitchResult? result = await _host.Coordinator.SwitchAsync(Profile("Desk", DeskModes), SwitchRequest.Default);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Blocked);
        asked.ShouldBeFalse();
        SwitchRecord record = _host.Coordinator.History[0];
        record.Ambiguous.ShouldBeTrue();
        record.MissingDisplays.ShouldBe(["Desk left", "Desk right"]);
        SwitchMessages.ForNotification(record).Text.ShouldBe(Loc.Format("Result_AmbiguousText", "Desk left, Desk right"));
    }

    [Fact]
    public void NoticeDisplayChange_OffersTheRest_OnlyForAnotherProfileWithMoreThanDisplays()
    {
        // HW-15: Windows restored the rig layout by itself when the ultrawide was switched on.
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig() with { KeepAwake = true };
        var offered = new List<Profile>();
        _host.Coordinator.RestoredByWindows += (_, profile) => offered.Add(profile);

        _host.Coordinator.NoticeDisplayChange(desk, rig).ShouldBeTrue();
        _host.Coordinator.NoticeDisplayChange(rig, rig).ShouldBeFalse();
        _host.Coordinator.NoticeDisplayChange(rig, null).ShouldBeFalse();
        _host.Coordinator.NoticeDisplayChange(rig, desk).ShouldBeFalse();

        offered.ShouldHaveSingleItem().ShouldBe(rig);
    }

    [Fact]
    public async Task KeepDisplays_AppliesTheRest_WithoutTouchingDisplaysOrAsking()
    {
        _host.Display.SetSnapshot(Snapshot(Attached(Ultrawide, activeMode: UltrawideMode), Attached(Tablet, activeMode: TabletMode)));
        Profile rig = Rig(confirm: true) with { KeepAwake = true };

        SwitchResult? result = await _host.Coordinator.SwitchAsync(rig, new SwitchRequest { KeepDisplays = true });

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        _host.Display.Applied.ShouldBeEmpty();
        await _host.Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
        _host.Coordinator.History[0].ProfileName.ShouldBe("Rig");
    }

    [Fact]
    public async Task History_KeepsTenNewest()
    {
        for (int i = 0; i <= 10; i++)
        {
            await _host.Coordinator.SwitchAsync(Rig() with { Name = "P" + i }, SwitchRequest.Default);
        }

        _host.Coordinator.History.Count.ShouldBe(10);
        _host.Coordinator.History[0].ProfileName.ShouldBe("P10");
        _host.Coordinator.History[^1].ProfileName.ShouldBe("P1");
    }

    [Fact]
    public async Task PartialResult_RemembersCatchUp_FailedKeepsIt_OtherClears()
    {
        Profile rig = Rig();
        _host.Display.SetSnapshot(DeskActive(tabletAttached: false));

        // The tablet is missing: applied (HW-03), and the catch-up follows the rig.
        (await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default))!.Outcome.ShouldBe(SwitchOutcome.Applied);

        // A failed switch to the same profile keeps following it.
        _host.Display.EnqueueApplyResults(87, 87);
        (await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default))!.Outcome.ShouldBe(SwitchOutcome.Failed);

        _host.Display.SetSnapshot(DeskActive());
        int applied = _host.Display.Applied.Count;
        await _host.Coordinator.CatchUpAsync();
        _host.Display.Applied.Count.ShouldBe(applied + 1);
        _host.Coordinator.History[0].Outcome.ShouldBe(SwitchOutcome.Applied);

        // Complete now: nothing more to catch up.
        await _host.Coordinator.CatchUpAsync();
        _host.Display.Applied.Count.ShouldBe(applied + 1);

        // Another switch after a partial one stops following.
        _host.Display.SetSnapshot(DeskActive(tabletAttached: false));
        await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default);
        await _host.Coordinator.SwitchAsync(Profile("Desk", DeskModes), SwitchRequest.Default);
        _host.Display.SetSnapshot(DeskActive());
        applied = _host.Display.Applied.Count;
        await _host.Coordinator.CatchUpAsync();
        _host.Display.Applied.Count.ShouldBe(applied);
    }

    [Fact]
    public async Task Stop_DuringConfirmation_RollsBackAndReturnsNull()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(async call =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(3));
            return ConfirmationResult.Confirmed;
        });

        Task<SwitchResult?> running = _host.Coordinator.SwitchAsync(Rig(confirm: true), SwitchRequest.Default);
        await reached.Task;
        (await _host.Coordinator.StopAsync(TimeSpan.FromSeconds(10))).ShouldBeTrue();

        (await running).ShouldBeNull();
        _host.Display.Applied.Count.ShouldBe(2);
        (await _host.Coordinator.SwitchAsync(Rig(), SwitchRequest.Default)).ShouldBeNull();
    }

    [Fact]
    public async Task Switch_WhileAppsWaitForDevice_IsNotBusy_AndCancelsTheWait()
    {
        var polling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        _host.Usb.PresentDeviceIds().Returns(call =>
        {
            polling.TrySetResult();
            _ = release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            return new HashSet<string>();
        });
        var appsCompleted = new TaskCompletionSource<SwitchRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Coordinator.FollowUpCompleted += (_, record) => appsCompleted.TrySetResult(record);
        bool busy = false;
        _host.Coordinator.BusyRejected += (_, _) => busy = true;
        Profile rig = Rig() with
        {
            Apps = [new AppAction { Path = "C:\\SimHub\\SimHubWPF.exe" }],
            AppsWaitForUsbDeviceId = "vid_0eb7&pid_0020",
            AppsWaitSeconds = 300,
        };

        SwitchResult? first = await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default);
        first.ShouldNotBeNull().Apps.ShouldBe(AppsOutcome.Pending);
        _host.Coordinator.IsSwitching.ShouldBeFalse();
        await polling.Task;

        SwitchResult? second = await _host.Coordinator.SwitchAsync(Profile("Desk", DeskModes), SwitchRequest.Default);
        release.Set();

        second.ShouldNotBeNull();
        busy.ShouldBeFalse();
        SwitchRecord apps = await appsCompleted.Task;
        apps.Apps.ShouldBe(AppsOutcome.Cancelled);
        _host.Coordinator.History.Single(r => r.ProfileName == "Rig").Apps.ShouldBe(AppsOutcome.Cancelled);
    }

    [Fact]
    public async Task Switch_DesktopSymbolsFollowTheResult_TheirProblemComesInASecondNotification()
    {
        // K-04: the symbols no longer hold up the result; what went wrong with them comes once they are done.
        _host.DesktopIcons.Outcome = DesktopIconOutcome.AutoArrange;
        var followUp = new TaskCompletionSource<SwitchRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Coordinator.FollowUpCompleted += (_, record) => followUp.TrySetResult(record);
        SwitchRecord? reported = null;
        _host.Coordinator.SwitchCompleted += (_, record) => reported = record;
        var layout = new DesktopIconLayout { Icons = [new DesktopIcon { Item = "::{645FF040-5081-101B-9F08-00AA002F954E}", X = 20, Y = 20 }] };

        SwitchResult? result = await _host.Coordinator.SwitchAsync(Rig() with { DesktopIcons = layout }, SwitchRequest.Default);

        result.ShouldNotBeNull().DesktopIcons.ShouldBe(DesktopIconOutcome.Pending);
        SwitchMessages.ForNotification(reported.ShouldNotBeNull()).Text.ShouldNotContain(Loc.Instance["Result_IconsAutoArrange"]);
        SwitchRecord done = await followUp.Task;
        done.DesktopIcons.ShouldBe(DesktopIconOutcome.AutoArrange);
        SwitchMessages.ForFollowUpNotification(done).ShouldNotBeNull().Text.ShouldBe(Loc.Instance["Result_IconsAutoArrange"]);
        _host.Coordinator.History.Single().DesktopIcons.ShouldBe(DesktopIconOutcome.AutoArrange);
    }

    [Fact]
    public async Task Check_WhileSwitchRuns_IsNotBusy()
    {
        var answer = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(answer.Task);
        bool busy = false;
        _host.Coordinator.BusyRejected += (_, _) => busy = true;

        Task<SwitchResult?> running = _host.Coordinator.SwitchAsync(Rig(confirm: true), SwitchRequest.Default);
        SwitchResult? check = await _host.Coordinator.CheckAsync(Profile("Desk", DeskModes));

        check.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.DryRun);
        busy.ShouldBeFalse();
        _host.Coordinator.IsSwitching.ShouldBeTrue();
        answer.SetResult(ConfirmationResult.Confirmed);
        (await running).ShouldNotBeNull();
        _host.Coordinator.History.Count.ShouldBe(1);
    }

    /// <summary>
    /// The triple with Surround and the desk without a setting: the way back to the desk switches the grid off, or the
    /// desk's monitors stay hidden inside it (findings K-09, U-05).
    /// </summary>
    [Fact]
    public async Task Switch_ProfileWithoutSurroundWhileAnotherUsesIt_SwitchesSurroundOff()
    {
        var grid = new SurroundGrid
        {
            Rows = 1,
            Columns = 3,
            Width = 2560,
            Height = 1440,
            Displays = [new SurroundDisplay { DisplayId = 1 }, new SurroundDisplay { DisplayId = 2 }, new SurroundDisplay { DisplayId = 3 }],
        };
        Profile triple = Rig() with { Id = Guid.NewGuid(), Name = "Triple", Surround = new SurroundSetting { Enabled = true, Grid = grid } };
        Profile desk = Rig() with { Id = Guid.NewGuid(), Name = "Desk" };
        await _host.Store.SaveAsync(triple, TestContext.Current.CancellationToken);
        await _host.Store.SaveAsync(desk, TestContext.Current.CancellationToken);
        await _host.Catalog.ReloadAsync(TestContext.Current.CancellationToken);
        _host.Surround.ActiveGrid = grid;

        SwitchResult? result = await _host.Coordinator.SwitchAsync(desk, SwitchRequest.Default);

        result.ShouldNotBeNull().Surround.ShouldBe(SurroundOutcome.Changed);
        _host.Surround.ActiveGrid.ShouldBeNull();
    }

    [Fact]
    public async Task TurnAllDisplaysOn_DuringACountdown_TakesTheSwitchBackFirst()
    {
        // The emergency hotkey while a countdown waits on a dark screen: the switch is cancelled and takes itself back,
        // and only then is every display turned on – in a call of its own, never next to the switch's.
        _host.Confirmation.ConfirmAsync(default!, default!, default, default).ReturnsForAnyArgs(call =>
        {
            var answer = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            call.Arg<CancellationToken>().Register(() => answer.TrySetResult(ConfirmationResult.Cancelled));
            return answer.Task;
        });
        Task<SwitchResult?> running = _host.Coordinator.SwitchAsync(Rig(confirm: true), SwitchRequest.Default);
        await UntilAsync(() => _host.Display.Applied.Count == 1);

        AllDisplaysOnResult? result = await _host.Coordinator.TurnAllDisplaysOnAsync();

        await running;
        result.ShouldNotBeNull().Outcome.ShouldBe(AllDisplaysOnOutcome.TurnedOn);
        _host.Display.Applied.Count.ShouldBe(3);
        _host.Display.Applied[1].Plan.Profile.Name.ShouldNotBe("All displays on");
        _host.Display.Applied[2].Options.ShouldBe(new ApplyOptions { UseDatabaseModes = true, SaveToDatabase = false });
        _host.Coordinator.IsSwitching.ShouldBeFalse();
    }

    [Fact]
    public async Task TurnAllDisplaysOn_WindowsRefuses_SwitchesToTheDefaultProfileWithoutAsking()
    {
        Profile desk = Profile("Desk", DeskModes, confirm: true);
        await _host.Store.SaveAsync(desk, CancellationToken.None);
        await _host.Catalog.ReloadAsync(CancellationToken.None);
        await _host.Settings.UpdateAsync(s => s with { DefaultProfileId = desk.Id }, CancellationToken.None);
        _host.Display.SetSnapshot(Snapshot([Attached(Ultrawide, activeMode: UltrawideMode), .. DeskModes.Select(m => Attached(m.Identity))]));
        _host.Display.EnqueueApplyResults(31, 31);
        AllDisplaysOnReport? report = null;
        _host.Coordinator.AllDisplaysOnCompleted += (_, r) => report = r;

        AllDisplaysOnResult? result = await _host.Coordinator.TurnAllDisplaysOnAsync();

        result.ShouldNotBeNull().Outcome.ShouldBe(AllDisplaysOnOutcome.Failed);
        report.ShouldNotBeNull().FallbackProfile.ShouldBe("Desk");
        _host.Coordinator.History.ShouldHaveSingleItem().ProfileName.ShouldBe("Desk");
        await _host.Confirmation.DidNotReceiveWithAnyArgs().ConfirmAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task TurnAllDisplaysOn_WindowsRefuses_WithoutADefaultProfile_OnlyReports()
    {
        _host.Display.SetSnapshot(Snapshot(Attached(Ultrawide, activeMode: UltrawideMode), Attached(Desk4K)));
        _host.Display.EnqueueApplyResults(31, 31);
        AllDisplaysOnReport? report = null;
        _host.Coordinator.AllDisplaysOnCompleted += (_, r) => report = r;

        await _host.Coordinator.TurnAllDisplaysOnAsync();

        report.ShouldNotBeNull().FallbackProfile.ShouldBeNull();
        _host.Coordinator.History.ShouldBeEmpty();
        _host.Display.Applied.Count.ShouldBe(2);
    }

    public void Dispose() => _host.Dispose();

    private static async Task UntilAsync(Func<bool> condition)
    {
        DateTime giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException("condition not met");
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
