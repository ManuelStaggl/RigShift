using System.Text.Json;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Windows.Display;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>Snapshot building on recorded CCD input, without hardware (analysis finding L-04).</summary>
public sealed class CcdSnapshotBuilderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Build_SkipsTargetsWithoutMonitorOrName_AndPutsAvailableFirst()
    {
        List<AttachedDisplay> displays = CcdSnapshotBuilder.Build(Load("ccd-synthetic.json"), Logger.None);

        displays.Select(d => d.Identity.FriendlyName).ShouldBe(["Fixture Ultrawide", "Fixture Sleeper"]);
    }

    [Fact]
    public void Build_ActiveTarget_UsesTargetModeRefreshHdrAndAllSources()
    {
        AttachedDisplay ultrawide = CcdSnapshotBuilder.Build(Load("ccd-synthetic.json"), Logger.None)[0];

        ultrawide.IsActive.ShouldBeTrue();
        ultrawide.IsAvailable.ShouldBeTrue();
        ultrawide.Identity.EdidManufacturerId.ShouldBe((ushort)0x1234);
        ultrawide.Identity.EdidProductCodeId.ShouldBe((ushort)0x5678);
        ultrawide.Identity.AdapterDevicePath.ShouldContain("#fixture#");

        DisplayAssignment mode = ultrawide.ActiveMode.ShouldNotBeNull();
        (mode.Width, mode.Height, mode.PositionX, mode.PositionY).ShouldBe((5120, 1440, 0, 0));
        (mode.RefreshNumerator, mode.RefreshDenominator).ShouldBe((239760u, 1000u));
        mode.IsPrimary.ShouldBeTrue();
        mode.Hdr.ShouldBe(true);

        CcdTargetHandle handle = ultrawide.NativeHandle.ShouldBeOfType<CcdTargetHandle>();
        handle.Adapter.ShouldBe(new AdapterLuid(0x1234, 0));
        handle.TargetId.ShouldBe(10u);
        handle.Sources.Select(s => s.Id).ShouldBe([0u, 1u]);
        handle.ActiveSource.ShouldBe(new CcdSource(new AdapterLuid(0x1234, 0), 0));
    }

    [Fact]
    public void Build_SleepingTarget_IsInactiveUnavailableWithoutEdid()
    {
        AttachedDisplay sleeper = CcdSnapshotBuilder.Build(Load("ccd-synthetic.json"), Logger.None)[1];

        sleeper.IsAvailable.ShouldBeFalse();
        sleeper.IsActive.ShouldBeFalse();
        sleeper.ActiveMode.ShouldBeNull();
        (sleeper.Identity.EdidManufacturerId, sleeper.Identity.EdidProductCodeId).ShouldBe(((ushort)0, (ushort)0));
    }

    [Fact]
    public void Build_ActivePathWithoutSourceMode_IsActiveWithoutMode()
    {
        CcdRawSnapshot raw = Load("ccd-synthetic.json");
        raw = raw with { Paths = [raw.Paths[0] with { SourceModeIndex = 99 }, .. raw.Paths.Skip(1)] };

        AttachedDisplay ultrawide = CcdSnapshotBuilder.Build(raw, Logger.None)[0];

        ultrawide.IsActive.ShouldBeTrue();
        ultrawide.ActiveMode.ShouldBeNull();
    }

    [Fact]
    public void Build_MissingAdapterPath_KeepsDisplayWithEmptyPath()
    {
        CcdRawSnapshot raw = Load("ccd-synthetic.json") with { Adapters = [] };

        CcdSnapshotBuilder.Build(raw, Logger.None).ShouldAllBe(d => d.Identity.AdapterDevicePath.Length == 0);
    }

    [Fact]
    public void ParseAdapter_IsTheInverseOfToString()
    {
        var luid = new AdapterLuid(0xDEADBEEF, -2);

        CcdSnapshotBuilder.ParseAdapter(luid.ToString()).ShouldBe(luid);
    }

    private static CcdRawSnapshot Load(string name) =>
        JsonSerializer.Deserialize<CcdRawSnapshot>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)), Json)
        ?? throw new InvalidOperationException($"Fixture {name} is empty.");
}
