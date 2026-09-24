using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class SurroundSectionTests
{
    private static readonly SurroundGrid Triple = new()
    {
        Rows = 1,
        Columns = 3,
        Width = 2560,
        Height = 1440,
        Displays = [new SurroundDisplay { DisplayId = 1 }, new SurroundDisplay { DisplayId = 2 }, new SurroundDisplay { DisplayId = 3 }],
    };

    [Fact]
    public void SavedSetting_OpensWithItsAnswer_AndTheGridItRan()
    {
        var section = new SurroundSection(
            new SurroundState { Availability = SurroundAvailability.Available }, new SurroundSetting { Enabled = true, Grid = Triple });

        section.Selected.ShouldNotBeNull().Key.ShouldBe("on");
        section.HintIsError.ShouldBeFalse();
        section.Build().ShouldBe(new SurroundSetting { Enabled = true, Grid = Triple });
    }

    [Fact]
    public void NoDriverAndNothingSaved_IsHiddenAndLeavesSurroundAlone()
    {
        var section = new SurroundSection(SurroundState.Unavailable(SurroundAvailability.NoDriver), saved: null);

        section.IsVisible.ShouldBeFalse();
        section.Choices.ShouldBeEmpty();
        section.Build().ShouldBeNull();
    }

    [Fact]
    public void Relabel_KeepsTheAnswer_AndIsNoChange()
    {
        var section = new SurroundSection(new SurroundState { Availability = SurroundAvailability.Available, Grids = [Triple] }, saved: null);
        section.Selected = section.Choices.First(c => c.Key == "off");
        int changes = 0;
        section.Changed += (_, _) => changes++;

        section.Relabel();

        section.Choices.Count.ShouldBe(3);
        section.Selected.ShouldNotBeNull().Key.ShouldBe("off");
        section.Hint.ShouldBe(Loc.Format("Editor_SurroundGrid", 3, 2560, 1440, Triple.TotalWidth, Triple.TotalHeight));
        changes.ShouldBe(0);

        section.Selected = section.Choices.First(c => c.Key == "on");
        changes.ShouldBe(1);
    }
}
