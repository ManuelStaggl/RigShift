using RigShift.Core.Profiles;
using RigShift.Windows.Display;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>
/// The translation between a profile's Surround grid and the driver's structure. It runs on every "Surround on", every
/// rollback and every journal restore, so whatever it drops is lost for good - before 4.0 that was the bezel correction
/// and the rotation of every triple rig.
/// </summary>
public sealed class NvSurroundGridTests
{
    /// <summary>A portrait triple with bezel correction, as the NVIDIA control panel builds it.</summary>
    private static SurroundGrid PortraitTriple(bool? corrected = true, int gap = -64) => new()
    {
        Rows = 1,
        Columns = 3,
        Width = 2560,
        Height = 1440,
        RefreshRateHz = 165,
        BezelCorrected = corrected,
        Displays =
        [
            new SurroundDisplay { DisplayId = 0x80061086, OverlapX = gap, Rotation = DisplayRotation.Rotate90 },
            new SurroundDisplay { DisplayId = 0x80061087, OverlapX = gap, Rotation = DisplayRotation.Rotate90 },
            new SurroundDisplay { DisplayId = 0x80061088, Rotation = DisplayRotation.Rotate90 },
        ],
    };

    [Fact]
    public void ToTopo_ThenToGrid_KeepsCorrectionOverlapsAndRotation()
    {
        SurroundGrid grid = PortraitTriple();

        SurroundGrid back = NvSurroundController.ToGrid(NvSurroundController.ToTopo(grid));

        back.Displays.ShouldBe(grid.Displays);
        (back with { Displays = grid.Displays }).ShouldBe(grid);
    }

    [Fact]
    public void ToTopo_OverlapsWithoutTheFlag_StillAskForTheCorrectedResolution()
    {
        // A driver that leaves the flag out of a running grid must not cost the correction on the next rebuild.
        MosaicGridTopoV2 topo = NvSurroundController.ToTopo(PortraitTriple(corrected: false));

        (topo.Flags & MosaicGridTopoV2.FlagApplyWithBezelCorrect).ShouldNotBe(0u);
        topo.Displays[0].OverlapX.ShouldBe(-64);
        topo.Displays[1].Rotation.ShouldBe(1u);
    }

    [Fact]
    public void ToTopo_GridWithoutCorrection_SetsNoFlag()
    {
        MosaicGridTopoV2 topo = NvSurroundController.ToTopo(PortraitTriple(corrected: false, gap: 0));

        topo.Flags.ShouldBe(0u);
    }

    [Fact]
    public void Singles_KeepTheRunningModeAndRotation_WithoutOverlap()
    {
        SurroundGrid[] singles = [.. NvSurroundController.Singles([PortraitTriple()])];

        singles.Length.ShouldBe(3);
        singles.ShouldAllBe(s => s.Rows == 1 && s.Columns == 1 && s.Width == 2560 && s.Height == 1440 && s.RefreshRateHz == 165);
        singles.ShouldAllBe(s => s.Displays.Count == 1 && s.Displays[0].OverlapX == 0 && s.Displays[0].Rotation == DisplayRotation.Rotate90);
        singles.Select(s => s.Displays[0].DisplayId).ShouldBe([0x80061086u, 0x80061087u, 0x80061088u]);
        NvSurroundController.ToTopo(singles[0]).Flags.ShouldBe(0u);
    }

    [Fact]
    public void Matches_GridWithLayout_ComparesOverlapsAndRotation()
    {
        SurroundGrid running = PortraitTriple(gap: -40);

        NvSurroundController.Matches(running, PortraitTriple()).ShouldBeFalse();
        NvSurroundController.Matches(PortraitTriple(), PortraitTriple()).ShouldBeTrue();
    }

    [Fact]
    public void Matches_GridSavedBefore40_TakesTheRunningGridAsItIs()
    {
        // Such a profile never recorded the correction; rebuilding without it would be the old bug in a new place.
        SurroundGrid saved = PortraitTriple(corrected: null, gap: 0) with
        {
            Displays = [.. PortraitTriple().Displays.Select(d => new SurroundDisplay { DisplayId = d.DisplayId })],
        };

        NvSurroundController.Matches(PortraitTriple(), saved).ShouldBeTrue();
    }
}
