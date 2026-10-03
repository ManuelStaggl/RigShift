using NSubstitute;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class WindowMemoryTests : IDisposable
{
    private static readonly PixelRect Left = new(0, 0, 800, 600);
    private static readonly PixelRect Right = new(1000, 0, 1800, 600);
    private static readonly PixelRect Elsewhere = new(50, 50, 400, 300);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rigshift-window-memory-tests", Guid.NewGuid().ToString("N"));
    private readonly AutoAdvanceTimeProvider _time = new();
    private readonly FakeWindowLayout _desktop = new() { MoveOnPlace = true };
    private readonly InMemoryWindowMemoryStore _store = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    /// <summary>The reason handles are kept: five windows of one program, and titles that say nothing.</summary>
    [Fact]
    public void MatchRemembered_PairsByHandle_WhereTitlesCannotTell()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.MatchRemembered(
            [Remembered(11, "explorer", "Downloads", Left), Remembered(12, "explorer", "Downloads", Right)],
            [Open(12, "explorer", "Downloads", Elsewhere), Open(11, "explorer", "Downloads", Elsewhere)]);

        matches.Single(m => m.Placement.Bounds == Left).Window!.Handle.ShouldBe(11);
        matches.Single(m => m.Placement.Bounds == Right).Window!.Handle.ShouldBe(12);
    }

    /// <summary>After a restart of Windows a handle may name any window; the program's name has to agree.</summary>
    [Fact]
    public void MatchRemembered_HandleNowBelongsToAnotherProgram_FallsBackToProgramAndTitle()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.MatchRemembered(
            [Remembered(11, "notepad", "notes.txt", Left)],
            [Open(11, "chrome", "Inbox", Elsewhere), Open(40, "notepad", "notes.txt", Elsewhere)]);

        matches.Single().Window!.Handle.ShouldBe(40);
    }

    [Fact]
    public void MatchRemembered_WindowClosedSince_HasNoWindow()
    {
        IReadOnlyList<WindowMatch> matches = WindowLayoutMatching.MatchRemembered(
            [Remembered(11, "notepad", "notes.txt", Left)],
            [Open(40, "chrome", "Inbox", Elsewhere)]);

        matches.Single().Window.ShouldBeNull();
    }

    [Fact]
    public void Capture_OnlyForAProfileThatRemembersAndIsBeingLeft()
    {
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Left));
        Profile desk = Desk();
        Profile rig = Rig();

        Memory().Capture(desk with { RememberWindows = false }, rig).ShouldBeNull();
        Memory().Capture(null, rig).ShouldBeNull();
        Memory().Capture(desk, desk).ShouldBeNull();

        RememberedWindows captured = Memory().Capture(desk, rig).ShouldNotBeNull();
        captured.Windows.Single().Handle.ShouldBe(11);
        captured.Windows.Single().Placement.ShouldBe(new WindowPlacement { ProcessName = "notepad", Title = "notes.txt", Bounds = Left });
    }

    [Fact]
    public async Task Restore_PutsBackWhatMoved_AndLeavesTheRestAlone()
    {
        Profile desk = Desk();
        _store.Profiles[desk.Id] = new RememberedWindows
        {
            Windows = [Remembered(11, "notepad", "notes.txt", Left), Remembered(12, "chrome", "Inbox", Right, WindowState.Maximized)],
        };
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Left));
        _desktop.Windows.Add(Open(12, "chrome", "Inbox", Elsewhere));

        WindowLayoutResult result = (await Memory().RestoreAsync(desk, Ct)).ShouldNotBeNull();

        result.ShouldBe(new WindowLayoutResult(1, 0, 0));
        _desktop.Placed.ShouldBe([((nint)12, Right, WindowState.Maximized)]);
    }

    /// <summary>Windows puts windows back itself a moment after a display returned – to where it thinks they belong.</summary>
    [Fact]
    public async Task Restore_WindowMovedAgainAfterwards_IsPutBackOnceMore()
    {
        Profile desk = Desk();
        _store.Profiles[desk.Id] = new RememberedWindows { Windows = [Remembered(11, "notepad", "notes.txt", Left)] };
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Elsewhere));
        _desktop.OnOpened = call =>
        {
            if (call == 2)
            {
                _desktop.Windows[0] = _desktop.Windows[0] with { Bounds = Elsewhere };
            }
        };

        WindowLayoutResult result = (await Memory().RestoreAsync(desk, Ct)).ShouldNotBeNull();

        result.Placed.ShouldBe(1);
        _desktop.Placed.Count.ShouldBe(2);
        _desktop.Windows.Single().Bounds.ShouldBe(Left);
    }

    [Fact]
    public async Task Restore_RefusedWindow_IsAskedOnce_AndClosedOneCountsAsMissing()
    {
        Profile desk = Desk();
        _store.Profiles[desk.Id] = new RememberedWindows
        {
            Windows = [Remembered(11, "taskmgr", "Task Manager", Left), Remembered(12, "notepad", "notes.txt", Right)],
        };
        _desktop.Windows.Add(Open(11, "taskmgr", "Task Manager", Elsewhere));
        _desktop.Refuse.Add(11);

        WindowLayoutResult result = (await Memory().RestoreAsync(desk, Ct)).ShouldNotBeNull();

        result.ShouldBe(new WindowLayoutResult(0, 1, 1));
        _desktop.OpenCalls.ShouldBe(1);
    }

    [Fact]
    public async Task Restore_ProfileDoesNotRememberOrWasNeverLeft_DoesNothing()
    {
        Profile desk = Desk();
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Elsewhere));

        (await Memory().RestoreAsync(desk, Ct)).ShouldBeNull();

        _store.Profiles[desk.Id] = new RememberedWindows { Windows = [Remembered(11, "notepad", "notes.txt", Left)] };
        (await Memory().RestoreAsync(desk with { RememberWindows = false }, Ct)).ShouldBeNull();
        _desktop.Placed.ShouldBeEmpty();
    }

    [Fact]
    public async Task Switch_AwayAndBack_RemembersTheWindowsAndPutsThemBack()
    {
        Profile desk = Desk();
        Profile rig = Rig();
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Left));
        SwitchOrchestrator orchestrator = Orchestrator();

        SwitchResult away = await orchestrator.SwitchAsync(rig, new SwitchRequest { Leaving = desk }, Ct);
        await away.TidyCompletion;

        _store.Profiles[desk.Id].Windows.Single().Placement.Bounds.ShouldBe(Left);
        _desktop.Placed.ShouldBeEmpty();

        // Windows moved it when its display went dark.
        _desktop.Windows[0] = _desktop.Windows[0] with { Bounds = Elsewhere };

        SwitchResult back = await orchestrator.SwitchAsync(desk, new SwitchRequest { Leaving = rig }, Ct);
        await back.TidyCompletion;

        _desktop.Windows.Single().Bounds.ShouldBe(Left);
    }

    /// <summary>A rejected switch never left the profile: what was remembered before stays.</summary>
    [Fact]
    public async Task Switch_NotConfirmed_RemembersNothing()
    {
        Profile desk = Desk();
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Left));
        ISwitchConfirmation confirmation = Substitute.For<ISwitchConfirmation>();
#pragma warning disable xUnit1051 // an argument matcher, not a token to observe
        confirmation.ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ConfirmationResult.Rejected);
#pragma warning restore xUnit1051

        SwitchResult result = await Orchestrator(confirmation).SwitchAsync(Rig(confirm: true), new SwitchRequest { Leaving = desk }, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _store.Profiles.ShouldBeEmpty();
    }

    /// <summary>Applying the active profile again must not throw the windows back to where they were last time.</summary>
    [Fact]
    public async Task Switch_ToTheProfileThatIsActive_LeavesItsWindowsAlone()
    {
        Profile desk = Desk();
        _store.Profiles[desk.Id] = new RememberedWindows { Windows = [Remembered(11, "notepad", "notes.txt", Left)] };
        _desktop.Windows.Add(Open(11, "notepad", "notes.txt", Elsewhere));

        SwitchResult result = await Orchestrator().SwitchAsync(desk, new SwitchRequest { Leaving = desk }, Ct);
        await result.TidyCompletion;

        _desktop.Placed.ShouldBeEmpty();
        _store.Profiles[desk.Id].Windows.Single().Placement.Bounds.ShouldBe(Left);
    }

    [Fact]
    public async Task Store_SaveThenLoad_RoundTrips_AlsoInANewInstance()
    {
        var id = Guid.NewGuid();
        var windows = new RememberedWindows
        {
            Windows = [Remembered(11, "notepad", "notes.txt", Left, WindowState.Maximized)],
            CapturedAt = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero),
        };

        using (var writer = new JsonWindowMemoryStore(_directory, Logger.None))
        {
            await writer.SaveAsync(id, windows, Ct);
        }

        using var reader = new JsonWindowMemoryStore(_directory, Logger.None);
        RememberedWindows loaded = (await reader.LoadAsync(id, Ct)).ShouldNotBeNull();

        loaded.CapturedAt.ShouldBe(windows.CapturedAt);
        loaded.Windows.Single().ShouldBe(windows.Windows.Single());
        (await reader.LoadAsync(Guid.NewGuid(), Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Store_UnreadableFile_ReadsAsNothingAndIsOverwritten()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, JsonWindowMemoryStore.FileName), "{ not json", Ct);
        var id = Guid.NewGuid();
        using var store = new JsonWindowMemoryStore(_directory, Logger.None);

        (await store.LoadAsync(id, Ct)).ShouldBeNull();
        await store.SaveAsync(id, new RememberedWindows { Windows = [Remembered(11, "notepad", "notes.txt", Left)] }, Ct);

        using var reader = new JsonWindowMemoryStore(_directory, Logger.None);
        (await reader.LoadAsync(id, Ct)).ShouldNotBeNull();
    }

    private static Profile Desk() => Profile("Desk", DeskModes) with { RememberWindows = true };

    private static RememberedWindow Remembered(long handle, string process, string title, PixelRect bounds, WindowState state = WindowState.Normal) =>
        new() { Handle = handle, Placement = new WindowPlacement { ProcessName = process, Title = title, Bounds = bounds, State = state } };

    private static OpenWindow Open(nint handle, string process, string title, PixelRect bounds) =>
        new(handle, process, title, bounds, WindowState.Normal);

    private WindowMemory Memory() => new(_desktop, _store, new SwitchOptions(), _time, Logger.None);

    private SwitchOrchestrator Orchestrator(ISwitchConfirmation? confirmation = null)
    {
        var options = new SwitchOptions { WindowRescueDelay = TimeSpan.Zero };
        return new SwitchOrchestrator(
            new FakeDisplayConfigurator(DeskActive()),
            Substitute.For<IAudioController>(),
            Substitute.For<IAppLauncher>(),
            Substitute.For<IUsbDeviceList>(),
            new FakePowerController(),
            new FakeDuckingPreference(),
            new InMemoryDuckingMemory(),
            Substitute.For<IWindowRescuer>(),
            new FakeDesktopIcons(),
            new FakeSurroundController(),
            confirmation ?? Substitute.For<ISwitchConfirmation>(),
            new InMemorySwitchJournal(),
            new TopologyPlanner(new TopologyPlannerOptions()),
            options,
            _time,
            Logger.None,
            new WindowMemory(_desktop, _store, options, _time, Logger.None));
    }
}
