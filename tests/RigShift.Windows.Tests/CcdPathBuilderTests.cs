using RigShift.Core.Profiles;
using RigShift.Windows.Display;
using Shouldly;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class CcdPathBuilderTests
{
    private static readonly AdapterLuid Gpu = new(0x1234, 0);

    [Fact]
    public void Build_StoredModes_OneActivePathAndSourceModePerDisplay()
    {
        var ultrawide = Target(1, activeSource: null, sources: [0, 1, 2, 3]);
        DisplayAssignment mode = Assignment(5120, 1440, 240, x: 0);

        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = CcdPathBuilder.Build([(ultrawide, mode)], databaseModes: false);

        DISPLAYCONFIG_PATH_INFO path = paths.Single();
        path.flags.ShouldBe(PInvoke.DISPLAYCONFIG_PATH_ACTIVE);
        path.targetInfo.id.ShouldBe(1u);
        path.targetInfo.refreshRate.Numerator.ShouldBe(240_000u);
        path.targetInfo.refreshRate.Denominator.ShouldBe(1000u);
        path.targetInfo.modeInfoIdx.ShouldBe(PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
        path.sourceInfo.modeInfoIdx.ShouldBe(0u);

        DISPLAYCONFIG_MODE_INFO source = modes.Single();
        source.infoType.ShouldBe(DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE);
        source.id.ShouldBe(path.sourceInfo.id);
        source.sourceMode.width.ShouldBe(5120u);
        source.sourceMode.height.ShouldBe(1440u);
    }

    [Fact]
    public void Build_DatabaseModes_PassesNoModesAndInvalidIndices()
    {
        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = CcdPathBuilder.Build(
            [(Target(1, null, [0, 1]), Assignment(1920, 1080, 60))], databaseModes: true);

        modes.ShouldBeEmpty();
        paths.Single().sourceInfo.modeInfoIdx.ShouldBe(PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
        paths.Single().targetInfo.modeInfoIdx.ShouldBe(PInvoke.DISPLAYCONFIG_PATH_MODE_IDX_INVALID);
    }

    [Fact]
    public void Build_KeepsActiveSources_AndGivesOthersDistinctFreeSources()
    {
        // Target 2 is active on source 0; target 1 lists source 0 first but must not clone onto it.
        var first = Target(1, activeSource: null, sources: [0, 1, 2]);
        var second = Target(2, activeSource: 0, sources: [0, 1, 2]);

        (DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes) = CcdPathBuilder.Build(
            [(first, Assignment(3840, 2160, 165)), (second, Assignment(1920, 1080, 100, x: 3840))], databaseModes: false);

        paths[0].sourceInfo.id.ShouldBe(1u);
        paths[1].sourceInfo.id.ShouldBe(0u);
        modes[(int)paths[1].sourceInfo.modeInfoIdx].sourceMode.position.x.ShouldBe(3840);
    }

    [Fact]
    public void Build_NoFreeSource_Throws()
    {
        Should.Throw<InvalidOperationException>(() => CcdPathBuilder.Build(
            [(Target(1, null, [0]), Assignment(1920, 1080, 60)), (Target(2, null, [0]), Assignment(1920, 1080, 60))], databaseModes: false));
    }

    private static CcdTargetHandle Target(uint id, uint? activeSource, uint[] sources) =>
        new(Gpu, id, sources.Select(s => new CcdSource(Gpu, s)).ToList(), activeSource is { } a ? new CcdSource(Gpu, a) : null);

    private static DisplayAssignment Assignment(int width, int height, uint hertz, int x = 0) =>
        new()
        {
            Identity = new DisplayIdentity { AdapterDevicePath = "adapter", TargetDevicePath = "target" },
            Width = width,
            Height = height,
            RefreshNumerator = hertz * 1000,
            RefreshDenominator = 1000,
            PositionX = x,
            PositionY = 0,
        };
}
