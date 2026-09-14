using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class DisplayNamesTests
{
    [Fact]
    public void Of_CombinesCustomNameAndModel()
    {
        DisplayNames.Of(Mode(DeskLeft, 1920, 1080, 100) with { CustomName = "  Left " }).ShouldBe("Left · Desk left");
        DisplayNames.Of(Mode(DeskLeft, 1920, 1080, 100)).ShouldBe("Desk left");
        DisplayNames.Label("Tablet", Tablet, "unnamed display").ShouldBe("Tablet · unnamed display");
    }

    [Fact]
    public void Normalize_TrimsShortensAndDropsBlanks()
    {
        DisplayNames.Normalize("   ").ShouldBeNull();
        DisplayNames.Normalize(null).ShouldBeNull();
        DisplayNames.Normalize(new string('x', 60))!.Length.ShouldBe(DisplayNames.MaxCustomNameLength);
    }

    [Fact]
    public void Propagate_UpdatesOnlyProfilesWithTheSameMonitor_IncludingRemovedNames()
    {
        Profile desk = Profile("Desk", [DeskModes[0], DeskModes[1] with { CustomName = "Old" }, DeskModes[2]]);
        Profile rig = Rig() with { Displays = [UltrawideMode, TabletMode with { CustomName = "Tablet" }] };
        Profile both = Profile("Both", [DeskModes[1] with { CustomName = "Left" }, DeskModes[2] with { CustomName = "Right" }]);

        IReadOnlyList<Profile> changed = DisplayNames.Propagate(both, [desk, rig, both]);

        Profile updated = changed.ShouldHaveSingleItem();
        updated.Id.ShouldBe(desk.Id);
        updated.Displays.Select(d => d.CustomName).ShouldBe([null, "Left", "Right"]);
    }

    [Fact]
    public void Propagate_RemovedName_ClearsItElsewhere()
    {
        Profile rig = Rig() with { Displays = [UltrawideMode, TabletMode with { CustomName = "Tablet" }] };
        Profile other = Profile("Other", [UltrawideMode, TabletMode]);

        Profile updated = DisplayNames.Propagate(other, [rig, other]).ShouldHaveSingleItem();

        updated.Displays[1].CustomName.ShouldBeNull();
    }

    [Fact]
    public void CurrentArrangement_TakesNamesFromPreviousFirst_ThenFromKnownProfiles()
    {
        Profile named = Profile("Named", [DeskModes[0] with { CustomName = "Main" }, DeskModes[1] with { CustomName = "Left" }]);

        IReadOnlyList<DisplayAssignment> arrangement = ProfileEditing.CurrentArrangement(
            DeskActive(), [DeskModes[1] with { CustomName = "Links" }], DisplayNames.Known([named]));

        arrangement.Single(d => d.Identity == Desk4K).CustomName.ShouldBe("Main");
        arrangement.Single(d => d.Identity == DeskLeft).CustomName.ShouldBe("Links");
        arrangement.Single(d => d.Identity == DeskRight).CustomName.ShouldBeNull();
    }

    [Fact]
    public void Known_PrefersTheRegistry_AndCoversMonitorsInNoProfile()
    {
        Profile profile = Profile("P", [DeskModes[1] with { CustomName = "Left" }]);
        IReadOnlyDictionary<string, string> registry = DisplayNames.WithName(
            DisplayNames.WithName(null, DeskLeft.TargetDevicePath, "Links"), Ultrawide.TargetDevicePath, "Rig");

        IReadOnlyDictionary<string, string> known = DisplayNames.Known([profile], registry);

        known[DeskLeft.TargetDevicePath].ShouldBe("Links");
        known[Ultrawide.TargetDevicePath].ShouldBe("Rig");
        DisplayNames.WithName(registry, Ultrawide.TargetDevicePath, " ").ContainsKey(Ultrawide.TargetDevicePath).ShouldBeFalse();
    }

    [Fact]
    public void Rename_ChangesOnlyProfilesWithTheMonitor()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig();

        IReadOnlyList<Profile> changed = DisplayNames.Rename([desk, rig], DeskLeft.TargetDevicePath, " Left ");

        changed.ShouldHaveSingleItem().Displays.Single(d => d.Identity == DeskLeft).CustomName.ShouldBe("Left");
        DisplayNames.Rename(changed, DeskLeft.TargetDevicePath, "Left").ShouldBeEmpty();
    }

    [Fact]
    public void Known_IgnoresBlankNames()
    {
        Profile profile = Profile("P", [DeskModes[0] with { CustomName = " " }, DeskModes[1] with { CustomName = "Left" }]);

        IReadOnlyDictionary<string, string> known = DisplayNames.Known([profile]);

        known.ShouldHaveSingleItem().Value.ShouldBe("Left");
    }
}
