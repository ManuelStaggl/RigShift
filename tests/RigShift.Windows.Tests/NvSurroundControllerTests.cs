using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Windows.Display;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Windows.Tests;

/// <summary>
/// The order of the driver calls behind "Surround on" and "Surround off", against a driver that records what it is asked.
/// Most Surround reports since 4.0 came from this sequencing, and no build machine has an NVIDIA card.
/// </summary>
public sealed class NvSurroundControllerTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly SurroundGrid Triple = new()
    {
        Rows = 1,
        Columns = 3,
        Width = 2560,
        Height = 1440,
        RefreshRateHz = 165,
        BezelCorrected = true,
        Displays =
        [
            new SurroundDisplay { DisplayId = 0x80061086, OverlapX = -64, Rotation = DisplayRotation.Rotate90 },
            new SurroundDisplay { DisplayId = 0x80061087, OverlapX = -64, Rotation = DisplayRotation.Rotate90 },
            new SurroundDisplay { DisplayId = 0x80061088, Rotation = DisplayRotation.Rotate90 },
        ],
    };

    private readonly FakeMosaic _driver = new();
    private readonly NvSurroundController _controller;

    public NvSurroundControllerTests()
    {
        _controller = new NvSurroundController(
            new FakeDisplayConfigurator(DeskActive()), Logger.None, () => (_driver, null), new AutoAdvanceTimeProvider());

        // A desktop without Surround: the driver reports every display as its own 1x1 grid.
        _driver.Current = [.. NvSurroundController.Singles([Triple])];
    }

    public void Dispose() => _controller.Dispose();

    private Task<SurroundApplyResult> On() => _controller.ApplyAsync(new SurroundSetting { Enabled = true, Grid = Triple }, Ct);

    private Task<SurroundApplyResult> Off() => _controller.ApplyAsync(new SurroundSetting { Enabled = false }, Ct);

    private const uint StrictFlags = NvApi.SetTopologyFlag.CurrentGpuTopology | NvApi.SetTopologyFlag.NoDriverReload;

    [Fact]
    public async Task On_FromTheDesktop_WakesEveryDisplayThenBuildsTheGrid()
    {
        SurroundApplyResult result = await On();

        result.Outcome.ShouldBe(SurroundOutcome.Changed);
        _driver.Sets.Count.ShouldBe(2);
        _driver.Sets[0].Grids.ShouldAllBe(g => g.Rows == 1 && g.Columns == 1);
        _driver.Sets[0].Grids.Select(g => g.Displays[0].DisplayId).ShouldBe(Triple.Displays.Select(d => d.DisplayId));
        NvSurroundController.Matches(_driver.Sets[1].Grids.ShouldHaveSingleItem(), Triple).ShouldBeTrue();
    }

    [Fact]
    public async Task On_WhileAnotherGridRuns_BuildsWithoutWaking()
    {
        _driver.Current = [Triple with { RefreshRateHz = 60 }];

        SurroundApplyResult result = await On();

        result.Outcome.ShouldBe(SurroundOutcome.Changed);
        _driver.Sets.ShouldHaveSingleItem().Grids.ShouldHaveSingleItem().RefreshRateHz.ShouldBe(165);
    }

    [Fact]
    public async Task On_WhenTheWantedGridRuns_LeavesItAlone()
    {
        _driver.Current = [Triple];

        (await On()).Outcome.ShouldBe(SurroundOutcome.Unchanged);
        _driver.Sets.ShouldBeEmpty();
    }

    /// <summary>RTX 2080, driver 610.88: no GPU topology to keep on a desktop without Surround, so the looser flags follow.</summary>
    [Fact]
    public async Task On_WhenTheDriverRefusesTheFlags_TriesTheLooserOnes()
    {
        _driver.SetAnswers.Enqueue(NvApi.Status.Ok);
        _driver.SetAnswers.Enqueue(NvApi.Status.NoActiveSliTopology);
        _driver.SetAnswers.Enqueue(NvApi.Status.DriverReloadRequired);

        SurroundApplyResult result = await On();

        result.Outcome.ShouldBe(SurroundOutcome.Changed);
        _driver.Sets.Skip(1).Select(s => s.Flags).ShouldBe([StrictFlags, NvApi.SetTopologyFlag.NoDriverReload, NvApi.SetTopologyFlag.None]);
    }

    [Fact]
    public async Task On_AnyOtherDriverErrorIsFinal()
    {
        _driver.SetAnswers.Enqueue(NvApi.Status.Ok);
        _driver.SetAnswers.Enqueue(NvApi.Status.ModeChangeFailed);

        SurroundApplyResult result = await On();

        result.Outcome.ShouldBe(SurroundOutcome.Failed);
        result.Message.ShouldNotBeNull().ShouldContain($"status {NvApi.Status.ModeChangeFailed}");
        _driver.Sets.Count.ShouldBe(2);
    }

    [Fact]
    public async Task On_StillBuildsTheGridWhenWakingFails()
    {
        _driver.SetAnswers.Enqueue(NvApi.Status.ModeChangeFailed);

        (await On()).Outcome.ShouldBe(SurroundOutcome.Changed);
        _driver.Sets.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Off_GivesEveryDisplayItsModeBackWithoutOverlap()
    {
        _driver.Current = [Triple];

        SurroundApplyResult result = await Off();

        result.Outcome.ShouldBe(SurroundOutcome.Changed);
        IReadOnlyList<SurroundGrid> singles = _driver.Sets.ShouldHaveSingleItem().Grids;
        singles.Count.ShouldBe(3);
        singles.ShouldAllBe(g => g.Width == 2560 && g.Height == 1440 && g.RefreshRateHz == 165);
        singles.ShouldAllBe(g => g.Displays[0].OverlapX == 0 && g.Displays[0].Rotation == DisplayRotation.Rotate90);
    }

    [Fact]
    public async Task Off_WithoutSurround_ChangesNothing()
    {
        (await Off()).Outcome.ShouldBe(SurroundOutcome.Unchanged);
        _driver.Sets.ShouldBeEmpty();
    }

    /// <summary>RTX 50 series, 2026: the driver answers "done" and does nothing.</summary>
    [Fact]
    public async Task Off_WhenTheDriverSaysDoneButNothingChanges_Fails()
    {
        _driver.Current = [Triple];
        _driver.AppliesSets = false;

        SurroundApplyResult result = await Off();

        result.Outcome.ShouldBe(SurroundOutcome.Failed);
        result.Message.ShouldNotBeNull().ShouldContain("did not change");
    }

    [Fact]
    public async Task Apply_WhenTheStateCannotBeRead_WritesNothing()
    {
        _driver.EnumAnswer = NvApi.Status.NoImplementation;

        (await On()).Outcome.ShouldBe(SurroundOutcome.NotAvailable);
        _driver.Sets.ShouldBeEmpty();
    }

    [Fact]
    public async Task Apply_WithoutAnNvidiaDriver_IsNotAvailable()
    {
        using var controller = new NvSurroundController(
            new FakeDisplayConfigurator(DeskActive()), Logger.None, () => (null, "no nvapi64.dll"), new AutoAdvanceTimeProvider());

        SurroundApplyResult result = await controller.ApplyAsync(new SurroundSetting { Enabled = true, Grid = Triple }, Ct);

        result.Outcome.ShouldBe(SurroundOutcome.NotAvailable);
        (await controller.QueryAsync(Ct)).Availability.ShouldBe(SurroundAvailability.NoDriver);
    }

    /// <summary>A driver that keeps the grids it is given and records every set call.</summary>
    private sealed class FakeMosaic : INvMosaic
    {
        public List<SurroundGrid> Current { get; set; } = [];

        public List<(uint Flags, IReadOnlyList<SurroundGrid> Grids)> Sets { get; } = [];

        /// <summary>Answers for the next set calls in order; <see cref="NvApi.Status.Ok"/> once empty.</summary>
        public Queue<int> SetAnswers { get; } = new();

        public bool AppliesSets { get; set; } = true;

        public int EnumAnswer { get; set; } = NvApi.Status.Ok;

        public string Describe(int status) => $"status {status}";

        public int EnumDisplayGrids(Span<MosaicGridTopoV2> grids, out uint count)
        {
            count = 0;
            if (EnumAnswer != NvApi.Status.Ok)
            {
                return EnumAnswer;
            }

            foreach (SurroundGrid grid in Current)
            {
                grids[(int)count++] = NvSurroundController.ToTopo(grid);
            }

            return NvApi.Status.Ok;
        }

        public int ValidateDisplayGrids(Span<MosaicGridTopoV2> grids, Span<MosaicDisplayTopoStatus> verdicts, uint flags) => NvApi.Status.Ok;

        public int SetDisplayGrids(Span<MosaicGridTopoV2> grids, uint flags)
        {
            List<SurroundGrid> set = [.. grids.ToArray().Select(topo => NvSurroundController.ToGrid(topo))];
            Sets.Add((flags, set));
            int answer = SetAnswers.Count > 0 ? SetAnswers.Dequeue() : NvApi.Status.Ok;
            if (answer == NvApi.Status.Ok && AppliesSets)
            {
                Current = set;
            }

            return answer;
        }

        public bool TryGetDisplayTarget(uint displayId, out uint adapterLow, out int adapterHigh, out uint targetId)
        {
            (adapterLow, adapterHigh, targetId) = (0, 0, 0);
            return false;
        }

        public void Dispose()
        {
        }
    }
}
