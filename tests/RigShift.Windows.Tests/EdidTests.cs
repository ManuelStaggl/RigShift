using RigShift.Core.Fov;
using RigShift.Windows.Display;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class EdidTests
{
    [Theory]
    [InlineData(@"\\?\DISPLAY#XEC2389#4&2f6eb3e3&0&UID20531#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"SYSTEM\CurrentControlSet\Enum\DISPLAY\XEC2389\4&2f6eb3e3&0&UID20531\Device Parameters")]
    [InlineData(@"\\?\DISPLAY#SAM749B#5&1a2b3c&0&UID4352#{guid}", @"SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM749B\5&1a2b3c&0&UID4352\Device Parameters")]
    public void RegistryKeyFor_TakesPnpIdAndInstanceFromThePath(string path, string expected)
    {
        Edid.RegistryKeyFor(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\?\DISPLAY#SAM749B")]
    [InlineData(@"\\?\USB#VID_0EB7&PID_0E04#123#{guid}")]
    [InlineData(@"\\?\DISPLAY#..\..#x#{guid}")]
    public void RegistryKeyFor_RefusesPathsThatAreNotAMonitor(string path)
    {
        Edid.RegistryKeyFor(path).ShouldBeNull();
    }

    [Fact]
    public void PictureSize_PrefersTheMillimetresOfTheFirstDetailedTiming()
    {
        byte[] edid = new byte[128];
        edid[21] = 60;
        edid[22] = 34;
        // 598 = 0x256 → low byte 0x56, high nibble 2; 336 = 0x150 → low byte 0x50, high nibble 1.
        edid[66] = 0x56;
        edid[67] = 0x50;
        edid[68] = 0x21;

        Edid.PictureSize(edid).ShouldBe(new ScreenSize(598, 336));
    }

    [Fact]
    public void PictureSize_FallsBackToCentimetres_AndReportsNothingForZero()
    {
        byte[] edid = new byte[128];
        edid[21] = 60;
        edid[22] = 34;
        Edid.PictureSize(edid).ShouldBe(new ScreenSize(600, 340));

        Edid.PictureSize(new byte[128]).ShouldBeNull();
        Edid.PictureSize(new byte[64]).ShouldBeNull();
    }
}
