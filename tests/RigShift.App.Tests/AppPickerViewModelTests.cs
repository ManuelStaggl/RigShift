using RigShift.App.ViewModels;
using RigShift.Windows.Apps;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class AppPickerViewModelTests
{
    private static readonly DiscoveredApp[] Found =
    [
        new("SimHub", @"C:\SimHub\SimHubWPF.exe", true),
        new("Crew Chief V4", @"C:\CrewChief\CrewChiefV4.exe", false),
        new("Steam", @"C:\Steam\steam.exe", false),
    ];

    [Fact]
    public async Task Load_ShowsAllApps_SearchFiltersAndSelectsTheBestMatch()
    {
        var viewModel = new AppPickerViewModel(() => Found, _ => null, Logger.None);

        await viewModel.LoadAsync();

        viewModel.IsLoading.ShouldBeFalse();
        viewModel.Apps.Count.ShouldBe(3);
        viewModel.CanChoose.ShouldBeFalse();

        viewModel.Query = "crew";
        viewModel.Apps.ShouldHaveSingleItem().Name.ShouldBe("Crew Chief V4");
        viewModel.Selected.ShouldNotBeNull().Path.ShouldBe(@"C:\CrewChief\CrewChiefV4.exe");
        viewModel.CanChoose.ShouldBeTrue();

        viewModel.Query = "nothing like it";
        viewModel.IsEmpty.ShouldBeTrue();
        viewModel.Selected.ShouldBeNull();
    }

    [Fact]
    public async Task Load_WhenListingFails_ShowsTheEmptyState()
    {
        var viewModel = new AppPickerViewModel(() => throw new UnauthorizedAccessException(), _ => null, Logger.None);

        await viewModel.LoadAsync();

        viewModel.IsEmpty.ShouldBeTrue();
    }
}
