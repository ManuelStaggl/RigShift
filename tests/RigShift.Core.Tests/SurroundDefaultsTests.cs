using RigShift.Core.Profiles;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

/// <summary>
/// "No Surround setting" leaves Surround alone only while no other profile uses it. The triple rig with Surround and
/// the desk without a setting is the common pair; before 4.0 the way back to the desk kept the grid, and the desk's
/// monitors stayed hidden inside it (findings K-09, U-05).
/// </summary>
public sealed class SurroundDefaultsTests
{
    private static readonly Profile Triple = Rig() with
    {
        Id = Guid.NewGuid(),
        Name = "Triple",
        Surround = new SurroundSetting
        {
            Enabled = true,
            Grid = new SurroundGrid { Rows = 1, Columns = 3, Width = 2560, Height = 1440, Displays = [new SurroundDisplay { DisplayId = 1 }] },
        },
    };

    private static readonly Profile Desk = Rig() with { Id = Guid.NewGuid(), Name = "Desk" };

    [Fact]
    public void Effective_NoSettingWhileAnotherProfileUsesSurround_IsOff()
    {
        SurroundDefaults.Effective(Desk, [Triple, Desk]).ShouldBe(SurroundDefaults.Off);
    }

    [Fact]
    public void Effective_NoSettingAndNobodyUsesSurround_LeavesItAlone()
    {
        SurroundDefaults.Effective(Desk, [Desk, Rig() with { Id = Guid.NewGuid(), Surround = SurroundDefaults.Off }]).ShouldBeNull();
    }

    [Fact]
    public void Effective_OwnSetting_Wins()
    {
        SurroundDefaults.Effective(Triple, [Triple, Desk]).ShouldBe(Triple.Surround);
    }

    /// <summary>The way back of an interrupted switch is no profile of the list: it restores what it recorded, nothing more.</summary>
    [Fact]
    public void Effective_ProfileOutsideTheList_KeepsItsOwnNothing()
    {
        SurroundDefaults.Effective(Rig() with { Id = Guid.NewGuid() }, [Triple, Desk]).ShouldBeNull();
    }
}
