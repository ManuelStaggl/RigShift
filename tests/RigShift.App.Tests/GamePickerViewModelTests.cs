using RigShift.App.Views;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class GamePickerViewModelTests
{
    private static readonly InstalledGame[] Found =
    [
        Game("iRacing", "266410"),
        Game("Assetto Corsa Competizione", "805550"),
        Game("Rocket League", "Sugar"),
    ];

    [Fact]
    public void Fill_ShowsEveryGame_SearchFiltersAndSelectsTheBestMatch()
    {
        var viewModel = new GamePickerViewModel();

        viewModel.Fill(Found);

        viewModel.IsLoading.ShouldBeFalse();
        viewModel.Games.Count.ShouldBe(3);
        viewModel.CanChoose.ShouldBeFalse();

        viewModel.Query = "assetto";
        viewModel.Games.ShouldHaveSingleItem().Name.ShouldBe("Assetto Corsa Competizione");
        viewModel.CanChoose.ShouldBeTrue();

        viewModel.Query = "nothing like it";
        viewModel.IsEmpty.ShouldBeTrue();
        viewModel.Selected.ShouldBeNull();
    }

    /// <summary>Picking several: what is ticked survives a search that hides the row again.</summary>
    [Fact]
    public void Checked_SurvivesFiltering()
    {
        var viewModel = new GamePickerViewModel(multiple: true);
        viewModel.Fill(Found);

        viewModel.Games[0].IsChecked = true;
        viewModel.CanChoose.ShouldBeTrue();

        viewModel.Query = "rocket";
        viewModel.Games.ShouldHaveSingleItem();
        viewModel.Games[0].IsChecked = true;

        viewModel.Checked().Select(g => g.Name).ShouldBe(["iRacing", "Rocket League"], ignoreOrder: true);
        viewModel.CanChoose.ShouldBeTrue();
    }

    /// <summary>One game at a time: the selection counts, ticks do not exist.</summary>
    [Fact]
    public void Single_UsesTheSelectionAndNotTheTicks()
    {
        var viewModel = new GamePickerViewModel();
        viewModel.Fill(Found);

        viewModel.Games[0].IsChecked = true;
        viewModel.CanChoose.ShouldBeFalse();

        viewModel.Selected = viewModel.Games[1];
        viewModel.CanChoose.ShouldBeTrue();
    }

    private static InstalledGame Game(string name, string target) =>
        new(name, new GameLaunch { Kind = GameLaunchKind.Steam, Target = target });
}
