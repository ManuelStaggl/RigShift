using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class UsbPowerSavingTests
{
    [Theory]
    [InlineData(true, 1, true)]
    [InlineData(null, 2, true)]
    [InlineData(false, 1, false)]
    [InlineData(true, 0, false)]
    [InlineData(null, 0, false)]
    public void ShouldWarn_WhenTheDeviceMaySleepAndTheSchemeAllowsIt(bool? schemeEnabled, int instancesWithPowerSaving, bool warn) =>
        UsbPowerSaving.ShouldWarn(new UsbPowerFindings
        {
            SelectiveSuspendEnabledOnAc = schemeEnabled,
            InstancesFound = 2,
            InstancesWithPowerSaving = instancesWithPowerSaving,
        }).ShouldBe(warn);

    public static TheoryData<object?, bool> FlagValues => new()
    {
        { 1, true },
        { 0, false },
        { 2, false },
        { new byte[] { 1, 0, 0, 0 }, true },
        { new byte[] { 1 }, true },
        { new byte[] { 0, 0, 0, 0 }, false },
        { "1", false },
        { null, false },
    };

    [Theory]
    [MemberData(nameof(FlagValues))]
    public void IsFlagEnabled_ReadsDwordAndBinary(object? value, bool enabled) =>
        UsbPowerSaving.IsFlagEnabled(value).ShouldBe(enabled);
}
