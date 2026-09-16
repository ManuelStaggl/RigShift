using System.Runtime.CompilerServices;
using RigShift.Windows.Display;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>
/// Pins down the NVAPI structure layout. NVAPI takes a structure's size as its version word, so a size that drifts by
/// one field is not a wrong number: the driver then writes a differently shaped record into our buffer. The expected
/// values are computed from NVIDIA's nvapi.h by hand, which is the point - they must not follow the C# code.
/// </summary>
public sealed class NvApiStructTests
{
    [Fact]
    public void DisplaySetting_MatchesTheHeader()
    {
        // version, width, height, bpp, freq
        Unsafe.SizeOf<MosaicDisplaySettingV1>().ShouldBe(5 * 4);
        MosaicDisplaySettingV1.Version1.ShouldBe(0x0001_0014u);
    }

    [Fact]
    public void GridTopoDisplay_MatchesTheHeader()
    {
        // version, displayId, overlapX, overlapY, rotation, cloneGroup, pixelShiftType
        Unsafe.SizeOf<MosaicGridTopoDisplayV2>().ShouldBe(7 * 4);
        MosaicGridTopoDisplayV2.Version2.ShouldBe(0x0002_001Cu);
    }

    [Fact]
    public void GridTopo_MatchesTheHeader()
    {
        // header (version, rows, columns, displayCount, flags) + 64 displays + display settings
        Unsafe.SizeOf<MosaicGridTopoV2>().ShouldBe((5 * 4) + (64 * 28) + 20);
        Unsafe.SizeOf<MosaicGridTopoV2>().ShouldBe(1832);
        MosaicGridTopoV2.StructVersion.ShouldBe(0x0002_0728u);
    }

    [Fact]
    public void TopoStatus_MatchesTheHeader()
    {
        // header (version, errorFlags, warningFlags, displayCount) + 128 displays of 4 words each
        Unsafe.SizeOf<MosaicDisplayTopoStatus>().ShouldBe((4 * 4) + (128 * 16));
        Unsafe.SizeOf<MosaicDisplayTopoStatus>().ShouldBe(2064);
        MosaicDisplayTopoStatus.StructVersion.ShouldBe(0x0001_0810u);
    }

    [Fact]
    public void DisplayIdInfo_MatchesTheHeader()
    {
        // version, adapterId (LUID, two words), targetId, four reserved words
        Unsafe.SizeOf<DisplayIdInfo>().ShouldBe(8 * 4);
        DisplayIdInfo.StructVersion.ShouldBe(0x0001_0020u);
    }

    [Fact]
    public void Problems_AreNamedOnePerFlag()
    {
        MosaicProblems.Describe(0).ShouldBeNull();
        MosaicProblems.Describe(1u << 2).ShouldBe("the displays have no resolution and refresh rate in common");
        MosaicProblems.Describe((1u << 2) | (1u << 5)).ShouldNotBeNull().ShouldContain("; ");
    }
}
