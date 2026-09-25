using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The start page: what is connected, what is on screen, which profile that is, and the recent switches.</summary>
public sealed class OverviewViewModelTests : IDisposable
{
    private readonly DemoServices _demo = new();

    public void Dispose() => _demo.Dispose();

    [Fact]
    public async Task Refresh_ListsEveryConnectedDisplay_DrawsTheActiveOnes()
    {
        using var ui = new DispatcherThread();
        await ui.RunAsync(async () =>
        {
            using ServiceProvider services = await _demo.StartAsync();
            OverviewViewModel overview = services.GetRequiredService<OverviewViewModel>();

            await overview.RefreshCommand.ExecuteAsync(null);

            overview.Displays.Count.ShouldBe(5);
            overview.LiveTopology.Count.ShouldBe(3, "the desk's three displays are on");
            overview.IsEmpty.ShouldBeFalse();
            overview.HasError.ShouldBeFalse();
            overview.Displays.Single(d => d.TargetDevicePath == Ultrawide.TargetDevicePath).ProfilesText.ShouldBe("Rig");
            overview.Displays.Single(d => d.TargetDevicePath == Desk4K.TargetDevicePath).ProfilesText.ShouldBe("Desk");
            overview.ActiveName.ShouldBe("Desk");
            overview.HasProfiles.ShouldBeTrue();
        });
    }

    [Fact]
    public async Task Refresh_DisplaysCannotBeRead_ShowsTheErrorCode()
    {
        using var ui = new DispatcherThread();
        await ui.RunAsync(async () =>
        {
            IDisplayConfigurator display = Substitute.For<IDisplayConfigurator>();
            display.QueryAsync(TestContext.Current.CancellationToken).ReturnsForAnyArgs<Task<DisplaySnapshot>>(_ => throw new Win32Exception(5));
            _demo.Display = display;
            using ServiceProvider services = await _demo.StartAsync();
            OverviewViewModel overview = services.GetRequiredService<OverviewViewModel>();

            await overview.RefreshCommand.ExecuteAsync(null);

            overview.HasError.ShouldBeTrue();
            overview.ErrorMessage.ShouldNotBeNull().ShouldContain("5");
        });
    }

    [Fact]
    public async Task Rename_ShowsTheNewNameInThePicture()
    {
        using var ui = new DispatcherThread();
        await ui.RunAsync(async () =>
        {
            using ServiceProvider services = await _demo.StartAsync();
            OverviewViewModel overview = services.GetRequiredService<OverviewViewModel>();
            await overview.RefreshCommand.ExecuteAsync(null);

            await overview.RenameAsync(overview.Displays.Single(d => d.TargetDevicePath == Desk4K.TargetDevicePath), "Middle");

            overview.LiveTopology.Single(d => d.Key == Desk4K.TargetDevicePath).Name.ShouldStartWith("Middle");
            overview.HasError.ShouldBeFalse();
        });
    }

    /// <summary>A check with --dry-run changed nothing on screen and has no place among the switches.</summary>
    [Fact]
    public async Task History_LeavesDryRunsOut()
    {
        using var ui = new DispatcherThread();
        await ui.RunAsync(async () =>
        {
            using ServiceProvider services = await _demo.StartAsync();
            OverviewViewModel overview = services.GetRequiredService<OverviewViewModel>();
            SwitchCoordinator coordinator = services.GetRequiredService<SwitchCoordinator>();

            coordinator.History.Insert(0, Record("Rig", SwitchOutcome.DryRun));
            overview.HasHistory.ShouldBeFalse();

            coordinator.History.Insert(0, Record("Rig", SwitchOutcome.Applied));
            overview.History.Select(h => h.ProfileName).ShouldBe(["Rig"]);
            overview.HasHistory.ShouldBeTrue();
        });
    }

    private static SwitchRecord Record(string profile, SwitchOutcome outcome) =>
        new(DateTimeOffset.UtcNow, profile, outcome, AudioOutcome.NotConfigured, AppsOutcome.NotConfigured, 1, TimeSpan.FromSeconds(2), null, null, []);
}
