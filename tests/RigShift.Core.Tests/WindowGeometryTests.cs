using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class WindowGeometryTests
{
    private static readonly PixelRect WorkArea = new(0, 0, 2560, 1400);

    [Fact]
    public void CenteredIn_KeepsTheSizeWhenItFits() =>
        WindowGeometry.CenteredIn(new PixelRect(-3000, 200, -2000, 900), WorkArea).ShouldBe(new PixelRect(780, 350, 1780, 1050));

    [Fact]
    public void CenteredIn_ShrinksAWindowLargerThanTheWorkArea() =>
        WindowGeometry.CenteredIn(new PixelRect(5120, 0, 10240, 1440), WorkArea).ShouldBe(WorkArea);

    [Fact]
    public void CenteredIn_UsesTheWorkAreaOffset()
    {
        // Taskbar on the left of a primary display that sits right of another one.
        var workArea = new PixelRect(2008, 0, 4560, 1440);

        PixelRect target = WindowGeometry.CenteredIn(new PixelRect(0, 0, 800, 600), workArea);

        target.ShouldBe(new PixelRect(2884, 420, 3684, 1020));
    }

    [Theory]
    [InlineData(0, 0, 0, 100, true)]
    [InlineData(10, 10, 5, 20, true)]
    [InlineData(0, 0, 1, 1, false)]
    public void IsEmpty_WithoutArea(int left, int top, int right, int bottom, bool empty) =>
        new PixelRect(left, top, right, bottom).IsEmpty.ShouldBe(empty);

    private static readonly PixelRect Normal = new(100, 100, 900, 700);
    private static readonly PixelRect Snapped = new(1280, 0, 2560, 1400);
    private static readonly PixelRect SecondDisplay = new(2560, 0, 5120, 1400);

    [Fact]
    public void ReportedBounds_SnappedWindowReportsItsCurrentRectangle() =>
        WindowGeometry.ReportedBounds(WindowState.Normal, Normal, Snapped, filledWorkAreaElsewhere: null).ShouldBe(Snapped);

    [Fact]
    public void ReportedBounds_NormalWindowWithoutRectangleKeepsItsNormalPosition() =>
        WindowGeometry.ReportedBounds(WindowState.Normal, Normal, currentRect: null, filledWorkAreaElsewhere: null).ShouldBe(Normal);

    [Fact]
    public void ReportedBounds_MaximizedOnAnotherDisplayReportsThatDisplay() =>
        WindowGeometry.ReportedBounds(WindowState.Maximized, Normal, Snapped, SecondDisplay)
            .ShouldBe(WindowGeometry.CenteredIn(Normal, SecondDisplay));

    [Fact]
    public void ReportedBounds_MaximizedOnItsOwnDisplayKeepsItsNormalPosition() =>
        WindowGeometry.ReportedBounds(WindowState.Maximized, Normal, Snapped, filledWorkAreaElsewhere: null).ShouldBe(Normal);

    [Fact]
    public void ReportedBounds_MinimizedKeepsItsNormalPosition() =>
        WindowGeometry.ReportedBounds(WindowState.Minimized, Normal, Snapped, SecondDisplay).ShouldBe(Normal);

    [Fact]
    public void PlacementSteps_MaximizedWindowOnAnotherDisplayIsRestoredFirst() =>
        WindowGeometry.PlacementSteps(WindowState.Maximized, targetOnActiveDisplay: true, maximizedNow: true, fillsAnotherDisplay: true)
            .ShouldBe([WindowState.Normal, WindowState.Maximized]);

    [Theory]
    [InlineData(WindowState.Maximized, false, true)]
    [InlineData(WindowState.Maximized, true, false)]
    [InlineData(WindowState.Normal, true, true)]
    [InlineData(WindowState.Minimized, true, true)]
    public void PlacementSteps_OtherwiseOneStep(WindowState target, bool maximizedNow, bool fillsAnotherDisplay) =>
        WindowGeometry.PlacementSteps(target, targetOnActiveDisplay: true, maximizedNow, fillsAnotherDisplay).ShouldBe([target]);

    [Fact]
    public void PlacementSteps_TargetOnNoActiveDisplayIsRefused() =>
        WindowGeometry.PlacementSteps(WindowState.Maximized, targetOnActiveDisplay: false, maximizedNow: true, fillsAnotherDisplay: true)
            .ShouldBeEmpty();
}
