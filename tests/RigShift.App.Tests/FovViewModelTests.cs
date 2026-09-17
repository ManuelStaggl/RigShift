using System.IO;
using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Fov;
using RigShift.Core.Settings;
using RigShift.Core.Tests;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class FovViewModelTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N")));
    private readonly SettingsService _settings;
    private readonly IDisplaySizeReader _sizes = Substitute.For<IDisplaySizeReader>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public FovViewModelTests()
    {
        _settings = new SettingsService(new JsonSettingsStore(Path.Combine(_directory, "settings.json"), Logger.None), Substitute.For<IAutostart>());
    }

    private static AttachedDisplay Attached(RigShift.Core.Profiles.DisplayAssignment mode, bool active = true) => new()
    {
        Identity = mode.Identity,
        IsAvailable = true,
        IsActive = active,
        ActiveMode = active ? mode : null,
        NativeHandle = new object(),
    };

    [Fact]
    public void Constructor_PicksThePrimaryDisplay_TakesItsSizeFromTheEdid()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        var viewModel = new FovViewModel(
            [Attached(TestDisplays.Mode(TestDisplays.Ultrawide, 5120, 1440, 240)), Attached(TestDisplays.Mode(TestDisplays.Desk4K, 3840, 2160, 60, primary: true)), Attached(TestDisplays.Mode(TestDisplays.Tablet, 1920, 1080, 60), active: false)],
            new Dictionary<string, string> { [TestDisplays.Desk4K.TargetDevicePath] = "Main" },
            _sizes, _settings, Logger.None);

        viewModel.Displays.Count.ShouldBe(2);
        viewModel.SelectedDisplay.ShouldNotBeNull().Name.ShouldStartWith("Main");
        viewModel.SizeIsMeasured.ShouldBeTrue();
        viewModel.DiagonalInches.ShouldBe(27.0, 0.05);
        viewModel.DistanceCm.ShouldBe(FovViewModel.DefaultDistanceCm);
        viewModel.IsTriple.ShouldBeFalse();
        viewModel.VerticalText.ShouldBe(Degrees(31.3));
        viewModel.HorizontalText.ShouldBe(Degrees(53.0));
        viewModel.TripleAngleText.ShouldBe("—");
        viewModel.GameRows.Single(r => r.Game == "Assetto Corsa Competizione").ValueText.ShouldBe("31");
    }

    [Fact]
    public void NoEdidSize_FallsBackTo27InchesWithTheResolutionsAspect_TypedDiagonalScales()
    {
        var viewModel = new FovViewModel(
            [Attached(TestDisplays.Mode(TestDisplays.Ultrawide, 5120, 1440, 240, primary: true))],
            new Dictionary<string, string>(), _sizes, _settings, Logger.None);

        viewModel.SizeIsMeasured.ShouldBeFalse();
        viewModel.DiagonalInches.ShouldBe(27.0, 0.05);
        (viewModel.CurrentScreen.WidthMm / viewModel.CurrentScreen.HeightMm).ShouldBe(32.0 / 9, 0.001);

        viewModel.DiagonalInches = 49;
        viewModel.CurrentScreen.DiagonalInches.ShouldBe(49, 0.001);
        viewModel.HorizontalText.ShouldBe(Degrees(FovCalculator.Calculate(new FovInput(viewModel.CurrentScreen, 600, false)).HorizontalDegrees));
    }

    [Fact]
    public async Task Triples_ShowAngleAndTotal_InputsAreRemembered()
    {
        _sizes.Read(TestDisplays.Desk4K).Returns(new ScreenSize(598, 336));
        var viewModel = new FovViewModel(
            [Attached(TestDisplays.Mode(TestDisplays.Desk4K, 3840, 2160, 60, primary: true))],
            new Dictionary<string, string>(), _sizes, _settings, Logger.None);

        viewModel.IsTriple = true;
        viewModel.BezelMm = 10;
        viewModel.DistanceCm = 60;

        viewModel.IsSingle.ShouldBeFalse();
        viewModel.TripleAngleText.ShouldBe(Degrees(54.5));
        viewModel.TripleTotalText.ShouldBe(Degrees(158.9));
        viewModel.GameRows.Single(r => r.Game == "iRacing").ValueText.ShouldBe("159");

        await viewModel.SaveInputsAsync(Ct);

        _settings.Current.FovTriple.ShouldBeTrue();
        _settings.Current.FovBezelMm.ShouldBe(10);
        _settings.Current.FovDistanceCm.ShouldBe(60);

        var again = new FovViewModel([Attached(TestDisplays.Mode(TestDisplays.Desk4K, 3840, 2160, 60, primary: true))], new Dictionary<string, string>(), _sizes, _settings, Logger.None);
        again.IsTriple.ShouldBeTrue();
        again.BezelMm.ShouldBe(10);
    }

    private static string Degrees(double value) => value.ToString("0.0", Loc.Instance.Culture) + "°";

    public void Dispose()
    {
        _settings.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
