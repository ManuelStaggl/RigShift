using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class ActiveProfileMatcherTests
{
    private readonly ActiveProfileMatcher _matcher = new(new TopologyPlanner(new TopologyPlannerOptions()));
    private readonly Profile _desk = Profile("Desk", DeskModes);
    private readonly Profile _rig = Rig();

    [Fact]
    public void FindActive_DeskActive_ReturnsDesk()
    {
        _matcher.FindActive([_rig, _desk], DeskActive()).ShouldBe(_desk);
    }

    [Fact]
    public void FindActive_RigActiveWithoutOptionalTablet_ReturnsRig()
    {
        DisplaySnapshot rigActive = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode), Attached(Desk4K));

        _matcher.FindActive([_desk, _rig], rigActive).ShouldBe(_rig);
    }

    [Fact]
    public void FindActive_IgnoresRefreshRateChanges()
    {
        DisplayAssignment slower = DeskModes[0] with { RefreshNumerator = 60_000 };
        DisplaySnapshot snapshot = Snapshot(
            Attached(Desk4K, activeMode: slower), Attached(DeskLeft, activeMode: DeskModes[1]), Attached(DeskRight, activeMode: DeskModes[2]));

        _matcher.FindActive([_desk], snapshot).ShouldBe(_desk);
    }

    [Fact]
    public void FindActive_ExtraActiveDisplay_MatchesNothing()
    {
        DisplaySnapshot snapshot = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode), Attached(Desk4K, activeMode: DeskModes[0] with { PositionX = 5120, IsPrimary = false }));

        _matcher.FindActive([_rig], snapshot).ShouldBeNull();
    }

    [Fact]
    public void FindActive_MovedDisplay_MatchesNothing()
    {
        DisplaySnapshot snapshot = Snapshot(
            Attached(Desk4K, activeMode: DeskModes[0]), Attached(DeskLeft, activeMode: DeskModes[1] with { PositionX = 3840 }), Attached(DeskRight, activeMode: DeskModes[2] with { PositionX = 7680 }));

        _matcher.FindActive([_desk], snapshot).ShouldBeNull();
    }

    [Fact]
    public void FindActive_ActiveOptionalDisplay_BelongsToTheProfileThatListsIt()
    {
        Profile ultrawideOnly = Profile("Ultrawide only", [UltrawideMode]);
        DisplaySnapshot snapshot = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode), Attached(Tablet, activeMode: TabletMode));

        _matcher.FindActive([ultrawideOnly, _rig], snapshot).ShouldBe(_rig);
    }
}
