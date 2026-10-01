using System.Drawing;
using RigShift.Windows.Ui;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>Windows that open larger than the monitor with the cursor, as WPF's <c>CenterScreen</c> does.</summary>
public sealed class NativeWindowFitTests
{
    /// <summary>A 1080p side monitor right of a 4K primary, both at 150 %: 1280 × 780 is 1920 × 1170 pixels.</summary>
    private static readonly Rectangle SideMonitor = new(3840, 0, 1920, 1032);

    private static readonly Rectangle PrimaryMonitor = new(0, 0, 3840, 2112);

    private static readonly Size AtOneFifty = new(1920, 1170);

    [Fact]
    public void Place_WindowInsideTheWorkArea_StaysWhereItIs()
    {
        NativeWindow.Place(new Rectangle(4160, 130, 1280, 780), SideMonitor, new Size(1280, 780), PrimaryMonitor, AtOneFifty)
            .ShouldBeNull();
    }

    /// <summary>The reported case: too tall for the side monitor, so it opens on the primary at its normal size.</summary>
    [Fact]
    public void Place_TooLargeForItsMonitor_GoesToThePrimaryAtItsNormalSize()
    {
        NativeWindow.Place(new Rectangle(3840, -69, 1920, 1170), SideMonitor, AtOneFifty, PrimaryMonitor, AtOneFifty)
            .ShouldBe((new Rectangle(960, 471, 1920, 1170), true));
    }

    [Fact]
    public void Place_FitsItsMonitorButOffTheEdge_IsCenteredThere()
    {
        NativeWindow.Place(new Rectangle(5000, 500, 1280, 780), SideMonitor, new Size(1280, 780), PrimaryMonitor, AtOneFifty)
            .ShouldBe((new Rectangle(4160, 126, 1280, 780), false));
    }

    [Fact]
    public void Place_AlreadyOnThePrimary_TakesTheWholeWorkArea()
    {
        var small = new Rectangle(0, 0, 1920, 1032);
        NativeWindow.Place(new Rectangle(0, -69, 1920, 1170), small, AtOneFifty, Rectangle.Empty, AtOneFifty)
            .ShouldBe((small, false));
    }

    [Fact]
    public void Place_TooLargeForThePrimaryToo_TakesTheWholeWorkAreaWhereItIs()
    {
        var smallPrimary = new Rectangle(0, 0, 1366, 728);
        NativeWindow.Place(new Rectangle(3840, -69, 1920, 1170), SideMonitor, AtOneFifty, smallPrimary, AtOneFifty)
            .ShouldBe((SideMonitor, false));
    }

    [Fact]
    public void Place_NoWorkArea_LeavesTheWindowAlone()
    {
        NativeWindow.Place(new Rectangle(0, 0, 1280, 780), Rectangle.Empty, new Size(1280, 780), PrimaryMonitor, AtOneFifty)
            .ShouldBeNull();
    }
}
