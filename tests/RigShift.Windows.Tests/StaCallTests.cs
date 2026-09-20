using RigShift.Windows.Shell;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class StaCallTests
{
    [Fact]
    public void Run_WorkReturns_HandsBackTheValueFromAnApartmentThread()
    {
        (bool finished, string? value, Exception? failure) = StaCall.Run(
            () => Thread.CurrentThread.GetApartmentState().ToString(), TimeSpan.FromSeconds(5));

        finished.ShouldBeTrue();
        value.ShouldBe(nameof(ApartmentState.STA));
        failure.ShouldBeNull();
    }

    /// <summary>An Explorer that hangs used to hang the switch with it: the join had no limit.</summary>
    [Fact]
    public void Run_WorkNeverReturns_GivesUpAfterTheLimit()
    {
        using var never = new ManualResetEventSlim();
        try
        {
            (bool finished, string? value, Exception? failure) = StaCall.Run<string>(
                () =>
                {
                    never.Wait(TestContext.Current.CancellationToken);
                    return "late";
                },
                TimeSpan.FromMilliseconds(100));

            finished.ShouldBeFalse();
            value.ShouldBeNull();
            failure.ShouldBeNull();
        }
        finally
        {
            never.Set();
        }
    }

    [Fact]
    public void Run_WorkThrows_ReportsTheFailureInsteadOfTearingDownTheProcess()
    {
        (bool finished, string? value, Exception? failure) = StaCall.Run<string>(
            () => throw new InvalidOperationException("no view"), TimeSpan.FromSeconds(5));

        finished.ShouldBeTrue();
        value.ShouldBeNull();
        failure.ShouldBeOfType<InvalidOperationException>();
    }
}
