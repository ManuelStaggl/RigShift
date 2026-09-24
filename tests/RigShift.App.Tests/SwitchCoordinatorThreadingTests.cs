using System.Collections.Concurrent;
using RigShift.App.Services;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>
/// A game session runs on a pool thread and switches from there. Whatever the coordinator tells the UI – that a switch
/// is running, the history, its result – has to arrive on the UI thread no matter who called.
/// </summary>
public sealed class SwitchCoordinatorThreadingTests : IDisposable
{
    private readonly SingleThreadContext _ui = new();
    private readonly AppTestHost _host;

    public SwitchCoordinatorThreadingTests() =>
        _host = _ui.Create(() => new AppTestHost(new FakeDisplayConfigurator(DeskActive())));

    [Fact]
    public async Task Switch_FromAPoolThread_TellsTheUiOnTheUiThread()
    {
        var threads = new ConcurrentBag<(string What, int Thread)>();
        _host.Coordinator.PropertyChanged += (_, e) => threads.Add((e.PropertyName!, Environment.CurrentManagedThreadId));
        _host.Coordinator.History.CollectionChanged += (_, _) => threads.Add(("History", Environment.CurrentManagedThreadId));
        _host.Coordinator.SwitchCompleted += (_, _) => threads.Add(("SwitchCompleted", Environment.CurrentManagedThreadId));
        IProfileSwitcher switcher = _host.Coordinator;

        SwitchResult? result = await Task.Run(
            () => switcher.SwitchAsync(Rig(), SwitchRequest.Default, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        threads.Select(t => t.What).ShouldContain(nameof(SwitchCoordinator.IsSwitching));
        threads.Select(t => t.What).ShouldContain("History");
        threads.Select(t => t.What).ShouldContain("SwitchCompleted");
        threads.Where(t => t.Thread != _ui.ThreadId).ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_FromAPoolThread_StillPassesTheExceptionOn()
    {
        // Thrown by the switch itself: the command line has to report it, not "busy".
        _host.Display.QueryExceptions.Enqueue(new InvalidOperationException("driver"));
        IProfileSwitcher switcher = _host.Coordinator;

        await Should.ThrowAsync<InvalidOperationException>(() => Task.Run(
            () => switcher.SwitchAsync(Rig(), SwitchRequest.Default, CancellationToken.None), TestContext.Current.CancellationToken));

        _host.Coordinator.History.ShouldHaveSingleItem().Outcome.ShouldBe(SwitchOutcome.Failed);
    }

    [Fact]
    public async Task Switch_ListenerThrows_TheSwitchStaysOneAppliedEntry()
    {
        _host.Coordinator.SwitchCompleted += (_, _) => throw new InvalidOperationException("listener");
        var seen = new List<SwitchRecord>();
        _host.Coordinator.SwitchCompleted += (_, record) => seen.Add(record);
        IProfileSwitcher switcher = _host.Coordinator;

        SwitchResult? result = await Task.Run(
            () => switcher.SwitchAsync(Rig(), SwitchRequest.Default, CancellationToken.None), TestContext.Current.CancellationToken);

        result.ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Applied);
        _host.Coordinator.History.ShouldHaveSingleItem().Outcome.ShouldBe(SwitchOutcome.Applied);
        seen.ShouldHaveSingleItem().Outcome.ShouldBe(SwitchOutcome.Applied);
    }

    public void Dispose()
    {
        _host.Dispose();
        _ui.Dispose();
    }
}
