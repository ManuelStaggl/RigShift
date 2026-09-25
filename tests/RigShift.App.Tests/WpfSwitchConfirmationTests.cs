using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Tests.Fakes;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class WpfSwitchConfirmationTests
{
    [Fact]
    public async Task WindowThatNeverAnswers_CountsAsTimedOut_AndIsClosed()
    {
        // A-08: a window that did not render after the display change never started its countdown; the switch waited for
        // good and every later one was refused as "already running".
        var never = new TaskCompletionSource<ConfirmationResult>();
        bool closed = false;

        ConfirmationResult result = await WpfSwitchConfirmation.WithDeadlineAsync(
            never.Task, TimeSpan.FromSeconds(25), new AutoAdvanceTimeProvider(), () => closed = true, TestContext.Current.CancellationToken);

        result.ShouldBe(ConfirmationResult.TimedOut);
        closed.ShouldBeTrue();
    }

    [Fact]
    public async Task AnswerInTime_IsPassedOn()
    {
        bool closed = false;

        ConfirmationResult result = await WpfSwitchConfirmation.WithDeadlineAsync(
            Task.FromResult(ConfirmationResult.Confirmed), TimeSpan.FromSeconds(25), new AutoAdvanceTimeProvider(), () => closed = true,
            TestContext.Current.CancellationToken);

        result.ShouldBe(ConfirmationResult.Confirmed);
        closed.ShouldBeFalse();
    }
}
