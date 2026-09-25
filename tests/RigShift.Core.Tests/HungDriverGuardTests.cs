using NSubstitute;
using RigShift.Core.Abstractions;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

// NSubstitute arrange/assert calls take a CancellationToken argument matcher, not a token to observe.
#pragma warning disable xUnit1051

public sealed class HungDriverGuardTests
{
    private readonly IDisplayConfigurator _driver = Substitute.For<IDisplayConfigurator>();
    private readonly HungDriverGuard _guard;

    public HungDriverGuardTests()
    {
        _driver.QueryAsync(default).ReturnsForAnyArgs(DeskActive());
        _guard = new HungDriverGuard(_driver, new SwitchOptions(), new AutoAdvanceTimeProvider(), Logger.None);
    }

    [Fact]
    public async Task Call_ThatAnswers_PassesThrough()
    {
        _driver.ApplyAsync(default!, default!, default).ReturnsForAnyArgs(0);

        (await _guard.ApplyAsync(Plan(), new ApplyOptions(), CancellationToken.None)).ShouldBe(0);
        (await _guard.QueryAsync(CancellationToken.None)).Displays.Count.ShouldBe(DeskActive().Displays.Count);
        _guard.IsHung.ShouldBeFalse();
    }

    [Fact]
    public async Task Call_StuckInTheDriver_MakesEveryLaterCallFailAtOnce_UntilItReturns()
    {
        // K-07: the query after a hung apply queued behind it in the kernel, without a limit, and held the switch lock.
        var stuck = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _driver.ApplyAsync(default!, default!, default).ReturnsForAnyArgs(stuck.Task);

        await Should.ThrowAsync<DisplayDriverHungException>(() => _guard.ApplyAsync(Plan(), new ApplyOptions(), CancellationToken.None));

        _guard.IsHung.ShouldBeTrue();
        await Should.ThrowAsync<DisplayDriverHungException>(() => _guard.QueryAsync(CancellationToken.None));
        await _driver.DidNotReceiveWithAnyArgs().QueryAsync(default);

        stuck.SetResult(0);
        _guard.IsHung.ShouldBeFalse();
        (await _guard.QueryAsync(CancellationToken.None)).ShouldNotBeNull();
    }

    [Fact]
    public void Hung_IsAWin32Error_SoEveryDisplayFailureHandlerCatchesIt()
    {
        var hung = new DisplayDriverHungException();

        DisplayApiFailure.Is(hung).ShouldBeTrue();
        hung.NativeErrorCode.ShouldBe(DisplayDriverHungException.ErrorTimeout);
    }

    private static TopologyPlan Plan() => new TopologyPlanner(new TopologyPlannerOptions()).Plan(Rig(), DeskActive());
}
