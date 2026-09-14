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
}
