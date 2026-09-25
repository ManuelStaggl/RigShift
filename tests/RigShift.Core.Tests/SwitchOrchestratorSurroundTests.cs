using NSubstitute;
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

/// <summary>
/// The Surround step of a switch. What matters here is the order: Surround decides which displays exist, so it runs
/// before the plan is made and is undone before the old arrangement is applied again.
/// </summary>
public sealed class SwitchOrchestratorSurroundTests
{
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

    private static readonly SurroundGrid TripleScreen = new()
    {
        Rows = 1,
        Columns = 3,
        Width = 1920,
        Height = 1080,
        RefreshRateHz = 60,
        Displays = [Display(1), Display(2), Display(3)],
    };

    [Fact]
    public async Task Switch_ProfileWithoutSurround_LeavesItAlone()
    {
        _surround.ActiveGrid = TripleScreen;
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(Rig(), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Surround.ShouldBe(SurroundOutcome.NotConfigured);
        _surround.Applied.ShouldBeEmpty();
        _surround.ActiveGrid.ShouldBe(TripleScreen);
    }

    [Fact]
    public async Task Switch_ProfileWantsSurroundOn_BuildsTheGridBeforeApplying()
    {
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(WithSurround(on: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Surround.ShouldBe(SurroundOutcome.Changed);
        _surround.ActiveGrid.ShouldBe(TripleScreen);
        display.Applied.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task Switch_GridAlreadyRuns_DoesNotRebuildIt()
    {
        _surround.ActiveGrid = TripleScreen;
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(WithSurround(on: true), SwitchRequest.Default, Ct);

        result.Surround.ShouldBe(SurroundOutcome.Unchanged);
    }

    [Fact]
    public async Task Switch_ProfileWantsSurroundOff_RemovesTheGrid()
    {
        _surround.ActiveGrid = TripleScreen;
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(WithSurround(on: false), SwitchRequest.Default, Ct);

        result.Surround.ShouldBe(SurroundOutcome.Changed);
        _surround.ActiveGrid.ShouldBeNull();
    }

    /// <summary>
    /// Windows lists the displays of a grid that was just taken apart seconds later. The switch waits for them quietly
    /// instead of asking the user to switch on monitors that are on (finding K-09).
    /// </summary>
    [Fact]
    public async Task Switch_DisplaysAppearLateAfterSurroundOff_WaitsWithoutAskingForThem()
    {
        _surround.ActiveGrid = TripleScreen;
        DisplaySnapshot inGrid = Snapshot(Attached(Desk4K));
        var display = new FakeDisplayConfigurator([inGrid, inGrid, inGrid, DeskActive()]);
        SwitchOrchestrator orchestrator = Create(display);
        bool asked = false;
        orchestrator.WaitingForDisplays += (_, _) => asked = true;

        SwitchResult result = await orchestrator.SwitchAsync(WithSurround(on: false), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        asked.ShouldBeFalse();
        display.QueryCount.ShouldBeGreaterThanOrEqualTo(4);
    }

    /// <summary>A grid the driver refuses is the end of the switch: the arrangement the profile describes cannot exist.</summary>
    [Fact]
    public async Task Switch_DriverRefusesTheGrid_BlocksAndSaysWhatTheDriverAnswered()
    {
        _surround.NextFailure = new SurroundApplyResult
        {
            Outcome = SurroundOutcome.Failed,
            Message = "The graphics driver could not switch Surround on: NVAPI_MODE_CHANGE_FAILED (-149).",
        };
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(WithSurround(on: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Blocked);
        result.Surround.ShouldBe(SurroundOutcome.Failed);
        result.Message.ShouldNotBeNull().ShouldContain("-149");
        display.Applied.ShouldBeEmpty();
        _journal.Entry.ShouldBeNull();
    }

    /// <summary>No NVIDIA driver is not a failure - the rest of the profile still applies.</summary>
    [Fact]
    public async Task Switch_WithoutNvidiaDriver_GoesOnWithoutSurround()
    {
        _surround.Availability = SurroundAvailability.NoDriver;
        var display = new FakeDisplayConfigurator(DeskActive());

        SwitchResult result = await Create(display).SwitchAsync(WithSurround(on: true), SwitchRequest.Default, Ct);

        result.Outcome.ShouldBe(SwitchOutcome.Applied);
        result.Surround.ShouldBe(SurroundOutcome.NotAvailable);
    }

    /// <summary>A rejected switch has to undo Surround too, or the displays of the old arrangement do not exist.</summary>
    [Fact]
    public async Task Switch_NotConfirmed_PutsSurroundBack()
    {
        _surround.ActiveGrid = TripleScreen;
        _confirmation.ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ConfirmationResult.Rejected);
        var display = new FakeDisplayConfigurator([DeskActive(), DeskActive(), DeskActive()]);

        SwitchResult result = await Create(display).SwitchAsync(
            WithSurround(on: false) with { SwitchWithoutAsking = false },
            SwitchRequest.Default with { DefaultConfirmTimeoutSeconds = 10 },
            Ct);

        result.Outcome.ShouldBe(SwitchOutcome.RolledBack);
        _surround.ActiveGrid.ShouldBe(TripleScreen);
    }

    /// <summary>The record for the next start carries the Surround state, so a crash can be undone completely.</summary>
    [Fact]
    public async Task Switch_WithSurround_RecordsTheStateToReturnTo()
    {
        _surround.ActiveGrid = TripleScreen;
        var display = new FakeDisplayConfigurator(DeskActive());

        await Create(display).SwitchAsync(WithSurround(on: false), SwitchRequest.Default, Ct);

        InterruptedSwitch recorded = _journal.Written.ShouldHaveSingleItem();
        recorded.Previous.Surround.ShouldNotBeNull().Enabled.ShouldBeTrue();
        recorded.Previous.Surround.Grid.ShouldBe(TripleScreen);
    }

    private static SurroundDisplay Display(uint id) => new() { DisplayId = id };

    private static Profile WithSurround(bool on) => Rig() with
    {
        Surround = new SurroundSetting { Enabled = on, Grid = on ? TripleScreen : null },
    };

    private SwitchOrchestrator Create(FakeDisplayConfigurator display) =>
        new(display, _audio, _apps, _usbDevices, _power, _ducking, _duckingMemory, _windows, new FakeDesktopIcons(), _surround, _confirmation, _journal,
            new TopologyPlanner(new TopologyPlannerOptions()), new SwitchOptions { WindowRescueDelay = TimeSpan.Zero }, _time, Logger.None);
}
