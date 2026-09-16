using RigShift.Core.Topology;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class TopologyLayoutTests
{
    private static TopologyDisplay Display(string key, int x, int y, int width, int height) =>
        new() { Key = key, X = x, Y = y, Width = width, Height = height };

    [Fact]
    public void Arrange_SingleDisplay_FillsTheLongerSideAndCenters()
    {
        // 16:9 into 100 × 100: width limits, 100 wide, 56.25 high, centered vertically.
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange([Display("a", 0, 0, 1920, 1080)], 100, 100, gap: 0);

        TopologyRect rect = rects.ShouldHaveSingleItem();
        rect.X.ShouldBe(0);
        rect.Width.ShouldBe(100);
        rect.Height.ShouldBe(56.25);
        rect.Y.ShouldBe((100 - 56.25) / 2);
    }

    [Fact]
    public void Arrange_UsesOneScaleForBothAxes()
    {
        // 3840 × 2160 into 480 × 100: height limits (100 / 2160), so the width scales with the same factor.
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange([Display("a", 0, 0, 3840, 2160)], 480, 100, gap: 0);

        TopologyRect rect = rects.ShouldHaveSingleItem();
        rect.Height.ShouldBe(100);
        rect.Width.ShouldBe(3840 * 100.0 / 2160, tolerance: 0.001);
        rect.Y.ShouldBe(0);
        rect.X.ShouldBe((480 - rect.Width) / 2, tolerance: 0.001);
    }

    [Fact]
    public void Arrange_NegativeCoordinates_ShiftIntoThePicture()
    {
        // Desk: 1080p left of the 4K primary (negative x), 1080p right of it. Bounding box 7680 × 2160.
        TopologyDisplay left = Display("left", -1920, 540, 1920, 1080);
        TopologyDisplay main = Display("main", 0, 0, 3840, 2160);
        TopologyDisplay right = Display("right", 3840, 540, 1920, 1080);

        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange([left, main, right], 768, 216, gap: 0);

        // Scale 0.1 on both axes.
        rects.Select(r => r.Display.Key).ShouldBe(["left", "main", "right"]);
        rects[0].X.ShouldBe(0);
        rects[0].Y.ShouldBe(54);
        rects[1].X.ShouldBe(192);
        rects[1].Width.ShouldBe(384);
        rects[2].X.ShouldBe(576);
        rects.Min(r => r.X).ShouldBeGreaterThanOrEqualTo(0);
        rects.Min(r => r.Y).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void Arrange_Gap_ShrinksEveryRectangleOnAllSides()
    {
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange(
            [Display("a", 0, 0, 1000, 500), Display("b", 1000, 0, 1000, 500)], 200, 50, gap: 3);

        rects[0].X.ShouldBe(3);
        rects[0].Y.ShouldBe(3);
        rects[0].Width.ShouldBe(94);
        rects[0].Height.ShouldBe(44);
        rects[1].X.ShouldBe(103);
        // The visible space between the two is twice the gap.
        (rects[1].X - (rects[0].X + rects[0].Width)).ShouldBe(6);
    }

    [Fact]
    public void Arrange_Label_OnlyWhenTheRectangleIsLargeEnough()
    {
        // Two displays in a 56 × 24 miniature: each 28 wide → too narrow for the default 24 × 14 label once the gap is off.
        IReadOnlyList<TopologyRect> tiny = TopologyLayout.Arrange(
            [Display("a", 0, 0, 1920, 1080), Display("b", 1920, 0, 1920, 1080)], 56, 24, gap: 1);
        tiny.ShouldAllBe(r => !r.ShowLabel);

        IReadOnlyList<TopologyRect> large = TopologyLayout.Arrange([Display("a", 0, 0, 1920, 1080)], 480, 200, gap: 3);
        large.ShouldAllBe(r => r.ShowLabel);

        IReadOnlyList<TopologyRect> strict = TopologyLayout.Arrange([Display("a", 0, 0, 1920, 1080)], 50, 30, gap: 0, minLabelWidth: 60, minLabelHeight: 30);
        strict.ShouldAllBe(r => !r.ShowLabel);
    }

    [Fact]
    public void Arrange_EmptyOrZeroSized_ReturnsNothing()
    {
        TopologyLayout.Arrange([], 100, 100, gap: 0).ShouldBeEmpty();
        TopologyLayout.Arrange([Display("a", 0, 0, 0, 0)], 100, 100, gap: 0).ShouldBeEmpty();
        TopologyLayout.Arrange([Display("a", 0, 0, 1920, 1080)], 0, 100, gap: 0).ShouldBeEmpty();
    }

    [Fact]
    public void Arrange_KeepsStateFlagsOnTheRectangle()
    {
        var display = new TopologyDisplay
        {
            Key = "a",
            X = 0,
            Y = 0,
            Width = 5120,
            Height = 1440,
            Number = 4,
            Name = "Odyssey",
            Mode = "5120×1440 @ 240 Hz",
            State = TopologyDisplayState.Missing,
            IsPrimary = true,
            IsOptional = true,
        };

        TopologyRect rect = TopologyLayout.Arrange([display], 100, 100, gap: 0).ShouldHaveSingleItem();

        rect.Display.ShouldBeSameAs(display);
        rect.Display.State.ShouldBe(TopologyDisplayState.Missing);
        rect.Display.IsPrimary.ShouldBeTrue();
        rect.Display.IsOptional.ShouldBeTrue();
    }

    [Fact]
    public void Arrange_ATooSmallPicture_NeverGoesBelowOnePixel()
    {
        IReadOnlyList<TopologyRect> rects = TopologyLayout.Arrange([Display("a", 0, 0, 1920, 1080)], 4, 4, gap: 3);

        TopologyRect rect = rects.ShouldHaveSingleItem();
        rect.Width.ShouldBe(1);
        rect.Height.ShouldBe(1);
    }
}
