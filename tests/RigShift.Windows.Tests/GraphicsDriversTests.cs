using RigShift.Windows.Display;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class GraphicsDriversTests
{
    [Theory]
    [InlineData("32.0.15.6094", "560.94")]
    [InlineData("31.0.15.3623", "536.23")]
    [InlineData("32.0.15.7602", "576.02")]
    public void NvidiaRelease_IsTheNumberNvidiaPublishes(string version, string release)
    {
        new GraphicsDriver("NVIDIA GeForce RTX 4080 SUPER", "NVIDIA", version).NvidiaRelease.ShouldBe(release);
    }

    [Theory]
    [InlineData("Advanced Micro Devices, Inc.", "31.0.24033.1003")]
    [InlineData("NVIDIA", "garbage")]
    [InlineData("NVIDIA", null)]
    public void NvidiaRelease_OnlyForARealNvidiaVersion(string provider, string? version)
    {
        new GraphicsDriver("Card", provider, version).NvidiaRelease.ShouldBeNull();
    }

    [Fact]
    public void ToString_NamesCardDriverAndRelease()
    {
        new GraphicsDriver("NVIDIA GeForce RTX 4080 SUPER", "NVIDIA", "32.0.15.6094").ToString()
            .ShouldBe("NVIDIA GeForce RTX 4080 SUPER, driver 32.0.15.6094 (560.94)");
    }

    [Fact]
    public void Read_ListsTheAdaptersOfThisMachine_WithoutThrowing()
    {
        // Every Windows has at least the basic or the remote display adapter.
        GraphicsDrivers.Read().ShouldNotBeEmpty();
    }
}
