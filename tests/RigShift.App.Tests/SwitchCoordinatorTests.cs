using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
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
        _host.Confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(answer.Task);
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
        _host.Confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(async call =>
        {
            reached.SetResult();
            await Task.Delay(Timeout.Infinite, call.ArgAt<CancellationToken>(2));
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
        _host.Coordinator.AppsCompleted += (_, record) => appsCompleted.TrySetResult(record);
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
    public async Task Check_WhileSwitchRuns_IsNotBusy()
    {
        var answer = new TaskCompletionSource<ConfirmationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Confirmation.ConfirmAsync(default!, default, default).ReturnsForAnyArgs(answer.Task);
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

    public void Dispose() => _host.Dispose();
}
