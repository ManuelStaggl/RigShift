using System.Runtime.InteropServices;
using Shouldly;
using Windows.Win32.Devices.Display;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class CcdStructTests
{
    // The legacy .display files store these structs as raw bytes (72 and 64 bytes, see docs/PLAN.md section 10).
    [Fact]
    public void PathInfo_Is72Bytes() => Marshal.SizeOf<DISPLAYCONFIG_PATH_INFO>().ShouldBe(72);

    [Fact]
    public void ModeInfo_Is64Bytes() => Marshal.SizeOf<DISPLAYCONFIG_MODE_INFO>().ShouldBe(64);
}
