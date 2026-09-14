using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class DisplayNumbersTests
{
    [Fact]
    public void Assign_WithWindowsNumbers_UsesThemInsteadOfPositions()
    {
        // HW-04: Windows counts the left display as 3, the 4K in the middle as 1.
        DisplaySnapshot snapshot = Snapshot(
            Attached(DeskLeft, activeMode: DeskModes[1]) with { WindowsNumber = 3 },
            Attached(Ultrawide),
            Attached(Desk4K, activeMode: DeskModes[0]) with { WindowsNumber = 1 },
            Attached(DeskRight, activeMode: DeskModes[2]) with { WindowsNumber = 2 });

        IReadOnlyList<(AttachedDisplay Display, int? Number)> numbers = DisplayNumbers.Assign(snapshot.Displays);

        numbers.Select(n => (n.Display.Identity.FriendlyName, n.Number)).ShouldBe(
        [
            ("Desk 4K", 1),
            ("Desk right", 2),
            ("Desk left", 3),
            ("Ultrawide 49", null),
        ]);
    }

    [Fact]
    public void Assign_WithoutNumberForEveryActiveDisplay_CountsLeftToRight()
    {
        DisplaySnapshot snapshot = Snapshot(
            Attached(Desk4K, activeMode: DeskModes[0]) with { WindowsNumber = 1 },
            Attached(DeskLeft, activeMode: DeskModes[1]),
            Attached(DeskRight, activeMode: DeskModes[2]) with { WindowsNumber = 1 });

        DisplayNumbers.Assign(snapshot.Displays).Select(n => (n.Display.Identity.FriendlyName, n.Number)).ShouldBe(
        [
            ("Desk left", 1),
            ("Desk 4K", 2),
            ("Desk right", 3),
        ]);
    }
}
