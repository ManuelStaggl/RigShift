using System.IO;
using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Fov;
using RigShift.Core.Tests;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class FovViewModelTests : IDisposable
{
    /// <summary>A curved 49-inch panel whose model name is in the table.</summary>
    private static readonly RigShift.Core.Profiles.DisplayIdentity OledG9 =
        TestDisplays.Identity(TestDisplays.Gpu, @"\\?\DISPLAY#SAM0009#TEST&9", 0x4C2D, 0x0009, "Odyssey G93SC");

    private readonly List<AppTestHost> _hosts = [];
    private readonly AppTestHost _host;
    private readonly IDisplaySizeReader _sizes = Substitute.For<IDisplaySizeReader>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FovViewModelTests()
    {
        _host = Host(
            TestDisplays.Attached(TestDisplays.Desk4K, activeMode: TestDisplays.Mode(TestDisplays.Desk4K, 2560, 1440, 165, primary: true)),
            TestDisplays.Attached(TestDisplays.Ultrawide, activeMode: TestDisplays.Mode(TestDisplays.Ultrawide, 5120, 1440, 240, x: 2560)),
            TestDisplays.Attached(TestDisplays.Tablet));
    }

    [Fact]
    public async Task Refresh_PicksThePrimaryDisplay_AndTakesItsSizeFromTheEdid()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        FovViewModel viewModel = Create();

        await viewModel.RefreshAsync();

        // The inactive tablet is not offered.
        viewModel.Displays.Count.ShouldBe(2);
        viewModel.SelectedDisplay.ShouldNotBeNull().Identity.ShouldBe(TestDisplays.Desk4K);
        viewModel.SizeIsMeasured.ShouldBeTrue();
        viewModel.DiagonalInches.ShouldBe(27.0, 0.05);
        viewModel.DistanceCm.ShouldBe(FovViewModel.DefaultDistanceCm);
        viewModel.VerticalText.ShouldBe(Degrees(31.3));
        viewModel.HorizontalText.ShouldBe(Degrees(53.0));
        viewModel.HasTrueHorizontal.ShouldBeFalse();
        viewModel.SimRows.Single(r => r.Id == "Acc").Value.ShouldBe("31");
        viewModel.PixelDensityText.ShouldBe(48.3.ToString("0.0", Loc.Instance.Culture));
    }

    [Fact]
    public async Task NoEdidSize_FallsBackTo27InchesWithTheResolutionsAspect_TypedDiagonalScales()
    {
        FovViewModel viewModel = Create();
        await viewModel.RefreshAsync();

        viewModel.SelectedDisplay = viewModel.Displays.Single(d => d.Identity == TestDisplays.Ultrawide);

        viewModel.SizeIsMeasured.ShouldBeFalse();
        (viewModel.CurrentScreen.WidthMm / viewModel.CurrentScreen.HeightMm).ShouldBe(32.0 / 9, 0.001);

        viewModel.DiagonalInches = 49;
        viewModel.CurrentScreen.DiagonalInches.ShouldBe(49, 0.001);
    }

    [Fact]
    public async Task Triples_ShowTheAngleAndTheTotal_AndFillTheSimsOwnFields()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        FovViewModel viewModel = Create();
        await viewModel.RefreshAsync();

        viewModel.IsTriple = true;
        viewModel.BezelMm = 10;
        viewModel.DistanceCm = 60;

        viewModel.IsSingle.ShouldBeFalse();
        viewModel.AngleText.ShouldBe(Degrees(54.5));
        viewModel.TotalText.ShouldBe(Degrees(162.0));
        viewModel.SimRows.Single(r => r.Id == "IRacing").Value.ShouldBe("158.9");
        viewModel.SimRows.Single(r => r.Id == "Ams2").Fields.ShouldNotBeEmpty();

        // A sim without triple rendering says so instead of pretending.
        viewModel.SimRows.Single(r => r.Id == "Wrc").HasWideHint.ShouldBeTrue();
    }

    [Fact]
    public async Task ManualAngle_OverridesTheCalculatedOne()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        FovViewModel viewModel = Create();
        await viewModel.RefreshAsync();

        viewModel.IsTriple = true;
        viewModel.BezelMm = 7;
        viewModel.AngleIsAutomatic = false;
        viewModel.AngleDegrees = 45;

        viewModel.AngleIsManual.ShouldBeTrue();
        viewModel.AngleText.ShouldBe(Degrees(45));
        viewModel.TotalText.ShouldBe(Degrees(153.6));
    }

    [Fact]
    public async Task Curvature_ComesFromTheModel_AndIsRememberedPerDisplay()
    {
        _sizes.Read(OledG9).Returns(new ScreenSize(1193, 336));
        AppTestHost host = Host(TestDisplays.Attached(OledG9, activeMode: TestDisplays.Mode(OledG9, 5120, 1440, 240, primary: true)));
        FovViewModel viewModel = Create(host);

        await viewModel.RefreshAsync();

        // The OLED G9 is 1800R, not the 1000R of the older one.
        viewModel.SelectedCurvature.ShouldNotBeNull().Key.ShouldBe("1800");
        viewModel.HasTrueHorizontal.ShouldBeTrue();

        viewModel.SelectedCurvature = viewModel.CurvatureChoices.Single(c => c.Key == "1000");
        await WaitForSettingsAsync(host, s => s.FovCurvatureMm is not null);

        host.Settings.Current.FovCurvatureMm.ShouldNotBeNull()[OledG9.TargetDevicePath].ShouldBe(1000);
    }

    [Fact]
    public async Task Inputs_AreRememberedAndReadBackByTheNextPage()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        FovViewModel viewModel = Create();
        await viewModel.RefreshAsync();

        viewModel.IsTriple = true;
        viewModel.BezelMm = 12;
        viewModel.DistanceCm = 70;
        viewModel.Comfort = true;
        await WaitForSettingsAsync(s => s.FovComfort && s.FovBezelMm == 12);

        FovViewModel again = Create();
        again.IsTriple.ShouldBeTrue();
        again.BezelMm.ShouldBe(12);
        again.DistanceCm.ShouldBe(70);
        again.Comfort.ShouldBeTrue();
    }

    [Fact]
    public async Task Warnings_AppearWhenTheArrangementCannotBeBuilt()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        FovViewModel viewModel = Create();
        await viewModel.RefreshAsync();

        viewModel.HasWarnings.ShouldBeFalse();

        viewModel.IsTriple = true;
        viewModel.DistanceCm = 35;

        viewModel.HasWarnings.ShouldBeTrue();
        viewModel.Warnings.ShouldContain(Loc.Instance["Fov_Warn_Angle"]);
    }

    private AppTestHost Host(params AttachedDisplay[] displays)
    {
        var host = new AppTestHost(new FakeDisplayConfigurator(TestDisplays.Snapshot(displays)));
        _hosts.Add(host);
        return host;
    }

    private FovViewModel Create() => Create(_host);

    private FovViewModel Create(AppTestHost host) =>
        new(host.Display, host.Catalog, _sizes, host.Settings, Logger.None);

    private Task WaitForSettingsAsync(Func<RigShift.Core.Settings.AppSettings, bool> until) => WaitForSettingsAsync(_host, until);

    [Fact]
    public void OwnGames_PinsTheLongestMatchingSim_NotEverySimItContains()
    {
        string[] sims = ["Assetto Corsa", "Assetto Corsa Competizione", "iRacing", "Le Mans Ultimate"];

        HashSet<string> pinned = FovSimRow.OwnGames(sims, ["ACC – Assetto Corsa Competizione", "iRacing", "Flight Simulator"]);

        pinned.ShouldBe(["Assetto Corsa Competizione", "iRacing"], ignoreOrder: true);
    }

    [Fact]
    public void OwnGames_WithoutGames_PinsNothing() =>
        FovSimRow.OwnGames(["iRacing"], []).ShouldBeEmpty();

    /// <summary>The page saves while the user types, so a test waits for the write instead of racing it.</summary>
    private static async Task WaitForSettingsAsync(AppTestHost host, Func<RigShift.Core.Settings.AppSettings, bool> until)
    {
        for (int i = 0; i < 100 && !until(host.Settings.Current); i++)
        {
            await Task.Delay(10, Ct);
        }

        until(host.Settings.Current).ShouldBeTrue();
    }

    private static string Degrees(double value) => value.ToString("0.0", Loc.Instance.Culture) + "°";

    public void Dispose() => _hosts.ForEach(h => h.Dispose());
}
