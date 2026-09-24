using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class DisplaySnapshotTests
{
    [Fact]
    public void FindActive_TellsTwinsApartByTheirTarget()
    {
        DisplaySnapshot snapshot = DeskActive();

        snapshot.FindActive(DeskRight).ShouldNotBeNull().ActiveMode.ShouldBe(DeskModes[2]);
    }

    [Fact]
    public void FindActive_IgnoresTheCaseOfTheTargetPath()
    {
        DisplayIdentity shouted = Desk4K with { TargetDevicePath = Desk4K.TargetDevicePath.ToUpperInvariant() };

        DeskActive().FindActive(shouted).ShouldNotBeNull().Identity.ShouldBe(Desk4K);
    }

    [Fact]
    public void FindActive_ADisplayThatIsOff_IsNotFound()
    {
        DeskActive().FindActive(Ultrawide).ShouldBeNull();
    }
}
