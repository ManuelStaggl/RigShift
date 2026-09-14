using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class UsbDeviceIdsTests
{
    [Theory]
    [InlineData(@"USB\VID_0EB7&PID_0020\6&2B1A5E3&0&4", "VID_0EB7&PID_0020")]
    [InlineData(@"USB\VID_046D&PID_C547&LAMPARRAY\7&1A2B", "VID_046D&PID_C547")]
    [InlineData(@"USB\VID_0a12&PID_4007&MI_00\7&abc&0&0000", "VID_0A12&PID_4007")]
    [InlineData("vid_0eb7&pid_0020", "VID_0EB7&PID_0020")]
    public void Normalize_ExtractsVendorAndProduct(string input, string expected) =>
        UsbDeviceIds.Normalize(input).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(@"USB\ROOT_HUB30\4&1234")]
    [InlineData("VID_12&PID_34")]
    public void Normalize_WithoutVendorAndProduct_IsNull(string? input) =>
        UsbDeviceIds.Normalize(input).ShouldBeNull();
}
