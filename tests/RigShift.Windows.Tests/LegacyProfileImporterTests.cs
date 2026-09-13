using System.Runtime.InteropServices;
using RigShift.Core.Legacy;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using RigShift.Windows.Legacy;
using Shouldly;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class LegacyProfileImporterTests
{
    private const string Gpu = @"\\?\PCI#VEN_10DE&DEV_0000#TEST#1";
    private const string Spacedesk = @"\\?\SWD#SPACEDESK#TEST#1";
    private const string UltrawidePath = @"\\?\DISPLAY#SAM0001#TEST&1";
    private const string TabletPath = @"\\?\DISPLAY#Default_Monitor#TEST&2";

    [Fact]
    public void Import_DecodesModesPositionsAndRefreshRate()
    {
        LegacyDisplayProfile legacy = new(
            [
                PathEntry(Gpu, UltrawidePath, "Ultrawide 49", sourceMode: 0, targetMode: 1),
                PathEntry(Spacedesk, TabletPath, string.Empty, sourceMode: 2, targetMode: uint.MaxValue),
            ],
            [
                SourceMode(5120, 1440, 0, 0, Gpu),
                TargetMode(239_761, 1000, Gpu),
                SourceMode(1920, 1080, 5120, 0, Spacedesk),
            ]);

        Profile profile = LegacyProfileImporter.Import("Rig", legacy, playback: null, live: null);

        profile.Name.ShouldBe("Rig");
        DisplayAssignment ultrawide = profile.Displays[0];
        ultrawide.ShouldSatisfyAllConditions(
            d => d.Width.ShouldBe(5120),
            d => d.Height.ShouldBe(1440),
            d => d.RefreshNumerator.ShouldBe(239_761u),
            d => d.IsPrimary.ShouldBeTrue(),
            d => d.IsOptional.ShouldBeFalse(),
            d => d.Identity.FriendlyName.ShouldBe("Ultrawide 49"));

        DisplayAssignment tablet = profile.Displays[1];
        tablet.PositionX.ShouldBe(5120);
        tablet.RefreshNumerator.ShouldBe(60u); // no target mode: falls back to the path refresh rate
        tablet.IsPrimary.ShouldBeFalse();
        tablet.IsOptional.ShouldBeTrue();
    }

    [Fact]
    public void Import_FillsEdidAndNameFromLiveSnapshot()
    {
        LegacyDisplayProfile legacy = new([PathEntry(Spacedesk, TabletPath, string.Empty, 0, uint.MaxValue)], [SourceMode(1920, 1080, 0, 0, Spacedesk)]);
        var live = new DisplaySnapshot
        {
            TakenAt = DateTimeOffset.UnixEpoch,
            Displays =
            [
                new AttachedDisplay
                {
                    Identity = new DisplayIdentity
                    {
                        AdapterDevicePath = Spacedesk,
                        TargetDevicePath = TabletPath,
                        EdidManufacturerId = 0x1111,
                        EdidProductCodeId = 0x2222,
                        FriendlyName = "Tablet",
                    },
                    IsAvailable = true,
                    IsActive = false,
                    NativeHandle = new object(),
                },
            ],
        };

        Profile profile = LegacyProfileImporter.Import("Rig", legacy, null, live);

        profile.Displays.Single().Identity.ShouldSatisfyAllConditions(
            i => i.EdidManufacturerId.ShouldBe((ushort)0x1111),
            i => i.FriendlyName.ShouldBe("Tablet"));
    }

    [Fact]
    public void Import_WrongStructSize_ThrowsFormatException()
    {
        LegacyDisplayProfile legacy = new(
            [new LegacyPathEntry(new byte[70], Gpu, Gpu, UltrawidePath, "x")],
            [SourceMode(1920, 1080, 0, 0, Gpu)]);

        Should.Throw<FormatException>(() => LegacyProfileImporter.Import("Desk", legacy, null, null));
    }

    [Fact]
    public void Import_SourceModeOutOfRange_ThrowsFormatException()
    {
        LegacyDisplayProfile legacy = new([PathEntry(Gpu, UltrawidePath, "x", sourceMode: 5, targetMode: uint.MaxValue)], []);

        Should.Throw<FormatException>(() => LegacyProfileImporter.Import("Desk", legacy, null, null));
    }

    private static LegacyPathEntry PathEntry(string adapter, string target, string name, uint sourceMode, uint targetMode)
    {
        var path = new DISPLAYCONFIG_PATH_INFO { flags = PInvoke.DISPLAYCONFIG_PATH_ACTIVE };
        path.sourceInfo.modeInfoIdx = sourceMode;
        path.targetInfo.modeInfoIdx = targetMode;
        path.targetInfo.rotation = DISPLAYCONFIG_ROTATION.DISPLAYCONFIG_ROTATION_IDENTITY;
        path.targetInfo.refreshRate = new DISPLAYCONFIG_RATIONAL { Numerator = 60, Denominator = 1 };
        return new LegacyPathEntry(ToBytes(path), adapter, adapter, target, name);
    }

    private static LegacyModeEntry SourceMode(uint width, uint height, int x, int y, string adapter)
    {
        var mode = new DISPLAYCONFIG_MODE_INFO { infoType = DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE };
        mode.sourceMode.width = width;
        mode.sourceMode.height = height;
        mode.sourceMode.position.x = x;
        mode.sourceMode.position.y = y;
        return new LegacyModeEntry(ToBytes(mode), adapter, string.Empty);
    }

    private static LegacyModeEntry TargetMode(uint numerator, uint denominator, string adapter)
    {
        var mode = new DISPLAYCONFIG_MODE_INFO { infoType = DISPLAYCONFIG_MODE_INFO_TYPE.DISPLAYCONFIG_MODE_INFO_TYPE_TARGET };
        mode.targetMode.targetVideoSignalInfo.vSyncFreq = new DISPLAYCONFIG_RATIONAL { Numerator = numerator, Denominator = denominator };
        return new LegacyModeEntry(ToBytes(mode), adapter, "target");
    }

    private static byte[] ToBytes<T>(T value)
        where T : unmanaged
    {
        var bytes = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(bytes, in value);
        return bytes;
    }
}
