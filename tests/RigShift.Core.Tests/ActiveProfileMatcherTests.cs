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
    public void FindActive_SameLayoutTwice_TheOneWithTheRunningRefreshRateWins()
    {
        // K-13: "Rig 144 Hz" and "Rig 240 Hz" – the one first in the list used to win whatever ran.
        Profile rig144 = Profile("Rig 144 Hz", [UltrawideMode with { RefreshNumerator = 144_000 }]);
        Profile rig240 = Profile("Rig 240 Hz", [UltrawideMode]);
        DisplaySnapshot running240 = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode));

        _matcher.FindActive([rig144, rig240], running240).ShouldBe(rig240);
        _matcher.FindActive([rig240, rig144], running240).ShouldBe(rig240);
    }

    [Fact]
    public void FindActive_ProfilesThatFitEquallyWell_TheLastAppliedWins()
    {
        Profile plain = Profile("Rig", [UltrawideMode]);
        Profile hdr = Profile("Rig HDR", [UltrawideMode with { Hdr = true }]);
        DisplaySnapshot hdrOn = Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = true }));

        _matcher.FindActive([plain, hdr], hdrOn, lastApplied: hdr.Id).ShouldBe(hdr);
        _matcher.FindActive([plain, hdr], hdrOn, lastApplied: plain.Id).ShouldBe(plain);
        _matcher.FindActive([plain, hdr], Snapshot(Attached(Ultrawide, activeMode: UltrawideMode with { Hdr = false })), lastApplied: hdr.Id).ShouldBe(plain);
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
