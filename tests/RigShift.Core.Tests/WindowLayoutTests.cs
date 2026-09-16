using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class WindowLayoutTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WindowPlacement Saved(string process, string? title = null, int left = 100, WindowState state = WindowState.Normal)
        => new() { ProcessName = process, Title = title, Bounds = new PixelRect(left, 0, left + 800, 600), State = state };

    private static OpenWindow Open(nint handle, string process, string title, int left = 0)
        => new(handle, process, title, new PixelRect(left, 0, left + 800, 600), WindowState.Normal);

    [Fact]
    public void Match_PairsByProcessAndTitle()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.Match(
            [Saved("SimHubWPF", "SimHub 9.4.2"), Saved("CrewChiefV4", "Crew Chief")],
            [Open(1, "CrewChiefV4", "Crew Chief"), Open(2, "SimHubWPF", "SimHub 9.4.2")]);

        matches.Single(m => m.Placement.ProcessName == "SimHubWPF").Window!.Handle.ShouldBe(2);
        matches.Single(m => m.Placement.ProcessName == "CrewChiefV4").Window!.Handle.ShouldBe(1);
    }

    /// <summary>Captions carry version numbers and lap counters; insisting on them would lose the window every time.</summary>
    [Fact]
    public void Match_AcceptsATitleThatOnlyStartsTheSame()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.Match(
            [Saved("SimHubWPF", "SimHub 9.4.2")],
            [Open(7, "SimHubWPF", "SimHub 9.5.0")]);

        matches.Single().Window!.Handle.ShouldBe(7);
    }

    /// <summary>Last resort: the program is right, so the window is better than nothing.</summary>
    [Fact]
    public void Match_FallsBackToTheProgramAlone()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.Match(
            [Saved("SimHubWPF", "Dash Studio")],
            [Open(7, "SimHubWPF", "Something else entirely")]);

        matches.Single().Window!.Handle.ShouldBe(7);
    }

    /// <summary>Two saved windows of one program must not both land on the first one.</summary>
    [Fact]
    public void Match_UsesEachWindowOnlyOnce()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.Match(
            [Saved("SimHubWPF", "SimHub 9.4.2", left: 0), Saved("SimHubWPF", "Dash Studio", left: 900)],
            [Open(1, "SimHubWPF", "SimHub 9.4.2"), Open(2, "SimHubWPF", "Dash Studio")]);

        matches.Select(m => m.Window!.Handle).Order().ShouldBe([1, 2]);
    }

    /// <summary>The exact title wins even when a weaker rule would have grabbed the window first.</summary>
    [Fact]
    public void Match_PrefersTheExactTitleOverTheProgramAlone()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.Match(
            [Saved("SimHubWPF", "Dash Studio")],
            [Open(1, "SimHubWPF", "SimHub 9.4.2"), Open(2, "SimHubWPF", "Dash Studio")]);

        matches.Single().Window!.Handle.ShouldBe(2);
    }

    [Fact]
    public void Match_ReportsASavedWindowThatIsNotOpen()
        => WindowLayoutMatching.Match([Saved("CrewChiefV4", "Crew Chief")], []).Single().Window.ShouldBeNull();

    [Fact]
    public async Task Restore_PutsEveryWindowBackWhereItBelongs()
    {
        var desktop = new FakeWindowLayout();
        desktop.Windows.Add(Open(1, "SimHubWPF", "SimHub 9.4.2"));
        desktop.Windows.Add(Open(2, "CrewChiefV4", "Crew Chief"));
        var layout = new WindowLayout
        {
            Windows = [Saved("SimHubWPF", "SimHub 9.4.2", left: 3840), Saved("CrewChiefV4", "Crew Chief", left: 5000, state: WindowState.Minimized)],
        };

        WindowLayoutResult result = await new WindowLayoutRestorer(desktop, new AutoAdvanceTimeProvider(), Logger.None)
            .RestoreAsync(layout, Ct);

        result.ShouldBe(new WindowLayoutResult(2, 0, 0));
        result.IsComplete.ShouldBeTrue();
        desktop.Placed.ShouldContain(p => p.Handle == 1 && p.Bounds.Left == 3840);
        desktop.Placed.ShouldContain(p => p.Handle == 2 && p.State == WindowState.Minimized);
    }

    /// <summary>A dashboard tool needs a few seconds to show its window; placing one that is not there yet does nothing.</summary>
    [Fact]
    public async Task Restore_WaitsForAWindowThatIsStillComingUp()
    {
        var desktop = new FakeWindowLayout();
        desktop.OnOpened = call =>
        {
            if (call == 3)
            {
                desktop.Windows.Add(Open(5, "SimHubWPF", "SimHub 9.4.2"));
            }
        };
        var layout = new WindowLayout { Windows = [Saved("SimHubWPF", "SimHub 9.4.2")] };

        WindowLayoutResult result = await new WindowLayoutRestorer(desktop, new AutoAdvanceTimeProvider(), Logger.None)
            .RestoreAsync(layout, Ct);

        result.Placed.ShouldBe(1);
        desktop.OpenCalls.ShouldBeGreaterThanOrEqualTo(3);
    }

    /// <summary>A program that never opens must not hold up the game beyond the wait.</summary>
    [Fact]
    public async Task Restore_GivesUpOnAWindowThatNeverTurnsUp()
    {
        var desktop = new FakeWindowLayout();
        desktop.Windows.Add(Open(1, "SimHubWPF", "SimHub 9.4.2"));
        var time = new AutoAdvanceTimeProvider();
        var layout = new WindowLayout { Windows = [Saved("SimHubWPF", "SimHub 9.4.2"), Saved("CoachDaveDelta", "Delta")] };

        WindowLayoutResult result = await new WindowLayoutRestorer(desktop, time, Logger.None).RestoreAsync(layout, Ct);

        result.ShouldBe(new WindowLayoutResult(1, 0, 1));
        result.IsComplete.ShouldBeFalse();
        time.Elapsed.ShouldBeGreaterThanOrEqualTo(WindowLayout.WindowWait);
    }

    /// <summary>An elevated program refuses to be moved; that is worth reporting, not worth failing over.</summary>
    [Fact]
    public async Task Restore_CountsAWindowThatRefusesToMove()
    {
        var desktop = new FakeWindowLayout();
        desktop.Windows.Add(Open(1, "SimHubWPF", "SimHub"));
        desktop.Refuse.Add(1);
        var layout = new WindowLayout { Windows = [Saved("SimHubWPF", "SimHub")] };

        WindowLayoutResult result = await new WindowLayoutRestorer(desktop, new AutoAdvanceTimeProvider(), Logger.None)
            .RestoreAsync(layout, Ct);

        result.ShouldBe(new WindowLayoutResult(0, 1, 0));
    }

    [Fact]
    public async Task Restore_OfAnEmptyLayoutDoesNothing()
    {
        var desktop = new FakeWindowLayout();

        (await new WindowLayoutRestorer(desktop, new AutoAdvanceTimeProvider(), Logger.None)
            .RestoreAsync(new WindowLayout(), Ct)).Total.ShouldBe(0);

        desktop.OpenCalls.ShouldBe(0);
    }
}
