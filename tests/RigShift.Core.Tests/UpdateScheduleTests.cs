using RigShift.Core.Updates;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 18, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// The check at login often runs before the Wi-Fi is up. With the next one a day later, a PC that is switched off
    /// every night never saw an update.
    /// </summary>
    [Theory]
    [InlineData(0, 24 * 60)]
    [InlineData(1, 1)]
    [InlineData(2, 5)]
    [InlineData(3, 30)]
    [InlineData(4, 24 * 60)]
    [InlineData(40, 24 * 60)]
    public void NextCheckIn_BacksOffAfterFailures_ThenFallsBackToTheDailyCheck(int failuresInARow, int minutes)
    {
        UpdateSchedule.NextCheckIn(failuresInARow).ShouldBe(TimeSpan.FromMinutes(minutes));
    }

    [Fact]
    public void IsDueAfterResume_WhenTheLastCheckFailed_OrIsADayOld_OrNeverHappened()
    {
        UpdateSchedule.IsDueAfterResume(lastSuccess: Now.AddHours(-2), Now, failuresInARow: 1).ShouldBeTrue();
        UpdateSchedule.IsDueAfterResume(lastSuccess: Now.AddHours(-25), Now, failuresInARow: 0).ShouldBeTrue();
        UpdateSchedule.IsDueAfterResume(lastSuccess: null, Now, failuresInARow: 0).ShouldBeTrue();
    }

    /// <summary>A laptop lid opened ten times a day must not mean ten requests to GitHub.</summary>
    [Fact]
    public void IsDueAfterResume_NotWhenARecentCheckSucceeded()
    {
        UpdateSchedule.IsDueAfterResume(lastSuccess: Now.AddHours(-2), Now, failuresInARow: 0).ShouldBeFalse();
    }
}
