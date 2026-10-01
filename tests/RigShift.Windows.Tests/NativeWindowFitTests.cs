using System.Drawing;
using RigShift.Windows.Ui;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>Windows that open larger than their monitor, as WPF does with mixed DPI.</summary>
public sealed class NativeWindowFitTests
{
    /// <summary>The 1080p side monitor at 100 % right of a 4K primary at 150 %.</summary>
    private static readonly Rectangle SideMonitor = new(3840, 0, 1920, 1040);

    [Fact]
    public void FitInto_WindowInsideTheWorkArea_StaysWhereItIs()
    {
        NativeWindow.FitInto(new Rectangle(4160, 130, 1280, 780), SideMonitor, 1280, 780).ShouldBeNull();
    }

    /// <summary>The reported case: 1280 × 780 sized at 150 % on a monitor at 100 %.</summary>
    [Fact]
    public void FitInto_WindowSizedForTheOtherDpi_GetsItsSizeHereAndIsCentered()
    {
        NativeWindow.FitInto(new Rectangle(3840, -65, 1920, 1170), SideMonitor, 1280, 780)
            .ShouldBe(new Rectangle(4160, 130, 1280, 780));
    }

    /// <summary>The window carries the primary monitor's 150 % while on the side monitor: it moves there at its own size.</summary>
    [Fact]
    public void FitInto_WindowOnAnotherMonitor_IsCenteredOnTheGivenWorkArea()
    {
        var primary = new Rectangle(0, 0, 3840, 2112);
        NativeWindow.FitInto(new Rectangle(3840, 0, 1920, 1032), primary, 1920, 1170)
            .ShouldBe(new Rectangle(960, 471, 1920, 1170));
    }

    [Fact]
    public void FitInto_WantedSizeLargerThanTheWorkArea_TakesTheWholeWorkArea()
    {
        NativeWindow.FitInto(new Rectangle(3700, -100, 2400, 1300), SideMonitor, 2400, 1300).ShouldBe(SideMonitor);
    }

    [Fact]
    public void FitInto_RightSizeButOffTheEdge_IsCentered()
    {
        NativeWindow.FitInto(new Rectangle(5000, 500, 1280, 780), SideMonitor, 1280, 780)
            .ShouldBe(new Rectangle(4160, 130, 1280, 780));
    }

    [Fact]
    public void FitInto_NoWorkArea_LeavesTheWindowAlone()
    {
        NativeWindow.FitInto(new Rectangle(0, 0, 1280, 780), Rectangle.Empty, 1280, 780).ShouldBeNull();
    }
}
