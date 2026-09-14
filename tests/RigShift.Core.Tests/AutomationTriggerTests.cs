using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class AutomationTriggerTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";
    private static readonly Guid Desk = Guid.NewGuid();
    private static readonly Guid Rig = Guid.NewGuid();
    private static readonly Guid Tv = Guid.NewGuid();

    private readonly AutomationTrigger _trigger = new();
    private TimeSpan _now = TimeSpan.FromMinutes(5);

    private static AutomationRule WheelbaseRule(ExitAction onExit = ExitAction.SwitchBack, Guid? exitProfile = null, bool skip = false) => new()
    {
        UsbDeviceId = Wheelbase,
        ProfileId = Rig,
        OnExit = onExit,
        ExitProfileId = exitProfile,
        SkipConfirmation = skip,
    };

    private static HashSet<string> Present(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<TriggerAction> Poll(AutomationRule rule, Guid? active, params string[] present) =>
        Poll([rule], active, present);

    private IReadOnlyList<TriggerAction> Poll(IReadOnlyList<AutomationRule> rules, Guid? active, params string[] present) =>
        Evaluate(rules, active, present).Actions;

    private TriggerEvaluation Evaluate(IReadOnlyList<AutomationRule> rules, Guid? active, params string[] present)
    {
        TriggerEvaluation evaluation = _trigger.Evaluate(rules, Present(present), active, _now);
        _now += TimeSpan.FromSeconds(2);
        return evaluation;
    }

    [Fact]
    public void StartBlocked_Disarm_RetriesOnceTheWaitIsOver()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem();

        TriggerEvent disarmed = _trigger.Disarm(rule, RetryMode.Later, _now).ShouldNotBeNull();

        disarmed.Delay.ShouldBe(AutomationTrigger.MinimumRetryDelay);
        Poll(rule, Desk, Wheelbase).ShouldBeEmpty();
        _now += AutomationTrigger.MinimumRetryDelay;
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem().Reason.ShouldBe(TriggerReason.Started);
    }

    [Fact]
    public void StartRejected_Disarm_NoLoopButReconnectWithinDelayStartsAgain()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem();

        _trigger.Disarm(rule, RetryMode.AfterReconnect, _now);

        _now += TimeSpan.FromMinutes(1);
        Poll(rule, Desk, Wheelbase).ShouldBeEmpty();
        Poll(rule, Desk).ShouldBeEmpty();
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem().Reason.ShouldBe(TriggerReason.Started);
    }

    [Fact]
    public void StartFailed_Disarm_DeviceGoneDoesNotSwitchBack()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);
        _trigger.Disarm(rule, RetryMode.AfterReconnect, _now);

        Poll(rule, Rig).ShouldBeEmpty();
        _now += AutomationTrigger.ExitDelayOf(rule);
        TriggerEvaluation evaluation = Evaluate([rule], Rig);

        evaluation.Actions.ShouldBeEmpty();
        evaluation.Events.ShouldHaveSingleItem().SkipReason.ShouldBe(ExitSkipReason.NotStartedByRule);
    }

    [Fact]
    public void ExitDelayZero_SinglePollGap_DoesNothing()
    {
        AutomationRule rule = WheelbaseRule() with { ExitDelaySeconds = 0 };
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem();

        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, Wheelbase).ShouldBeEmpty();

        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Desk, TriggerReason.Ended));
    }

    [Fact]
    public void TwoRulesSameDevice_BothStart()
    {
        AutomationRule rig = WheelbaseRule();
        AutomationRule tv = WheelbaseRule() with { Id = Guid.NewGuid(), ProfileId = Tv };
        Poll([rig, tv], Desk);

        Poll([rig, tv], Desk, Wheelbase).Select(a => a.ProfileId).ShouldBe([Rig, Tv]);
    }

    [Fact]
    public void NoActiveProfileAtStart_SwitchBackDoesNothing_Logged()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, null);
        TriggerEvaluation started = Evaluate([rule], null, Wheelbase);
        started.Actions.ShouldHaveSingleItem();
        started.Events.Select(e => e.Kind).ShouldBe([TriggerEventKind.DeviceConnected, TriggerEventKind.NoPreviousProfile]);

        Poll(rule, Rig).ShouldBeEmpty();
        _now += AutomationTrigger.ExitDelayOf(rule);
        TriggerEvaluation evaluation = Evaluate([rule], Rig);

        evaluation.Actions.ShouldBeEmpty();
        evaluation.Events.ShouldHaveSingleItem().SkipReason.ShouldBe(ExitSkipReason.NoPreviousProfile);
    }

    [Fact]
    public void Reset_DuringExitDelay_ClearsGoneSince()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);
        Poll(rule, Rig).ShouldBeEmpty();

        _trigger.Reset();
        _now += TimeSpan.FromMinutes(5);

        Evaluate([rule], Rig).Events.ShouldHaveSingleItem().Kind.ShouldBe(TriggerEventKind.Baseline);
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void Events_DescribeGoneAndBack()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Evaluate([rule], Desk, Wheelbase).Events.ShouldHaveSingleItem().Kind.ShouldBe(TriggerEventKind.DeviceConnected);

        TriggerEvent gone = Evaluate([rule], Rig).Events.ShouldHaveSingleItem();
        gone.Kind.ShouldBe(TriggerEventKind.DeviceGone);
        gone.Delay.ShouldBe(TimeSpan.FromSeconds(10));
        Evaluate([rule], Rig, Wheelbase).Events.ShouldHaveSingleItem().Kind.ShouldBe(TriggerEventKind.DeviceBack);
    }

    [Fact]
    public void DeviceConnects_SwitchesToRuleProfile()
    {
        AutomationRule rule = WheelbaseRule(skip: true);
        Poll(rule, Desk).ShouldBeEmpty();

        IReadOnlyList<TriggerAction> actions = Poll(rule, Desk, Wheelbase);

        actions.ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Rig, TriggerReason.Started));
        actions[0].SkipConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void DeviceConnectedAtFirstPoll_DoesNotSwitch()
    {
        AutomationRule rule = WheelbaseRule();

        Poll(rule, Desk, Wheelbase).ShouldBeEmpty();
        Poll(rule, Desk, Wheelbase).ShouldBeEmpty();
    }

    [Fact]
    public void ProfileAlreadyActive_DoesNotSwitchAndNotBackOnExit()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, Wheelbase).ShouldBeEmpty();

        GoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceGone_SwitchesBackAfterDelay()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);

        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(5);
        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(5);

        Poll(rule, Rig).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Desk, TriggerReason.Ended));
        GoneLongEnough(rule, Desk).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceBackWithinDelay_NeitherSwitchesBackNorAgain()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);
        Poll(rule, Rig);

        Poll(rule, Rig, Wheelbase).ShouldBeEmpty();
        _now += TimeSpan.FromMinutes(1);
        Poll(rule, Rig, Wheelbase).ShouldBeEmpty();

        GoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Desk);
    }

    [Fact]
    public void UserPickedAnotherProfileMeanwhile_ExitDoesNothing()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.SwitchTo, Desk);
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);

        GoneLongEnough(rule, Tv).ShouldBeEmpty();
    }

    [Fact]
    public void ExitSwitchTo_SwitchesToChosenProfile()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.SwitchTo, Tv);
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);

        GoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Tv);
    }

    [Fact]
    public void ExitStay_DoesNothing()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.Stay);
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase);

        GoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceMatchedByVendorAndProductIdOnly()
    {
        var rule = new AutomationRule { UsbDeviceId = @"USB\VID_0eb7&PID_0020\5&1a2b", ProfileId = Rig };
        Poll(rule, Desk);

        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem();
    }

    [Fact]
    public void DeviceOfRuleChangedWhileNewDeviceIsConnected_DoesNotSwitch()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk, Dongle);

        Poll(rule with { UsbDeviceId = Dongle }, Desk, Dongle).ShouldBeEmpty();
    }

    [Fact]
    public void RuleAddedWhileDeviceIsConnected_DoesNotSwitch()
    {
        AutomationRule other = WheelbaseRule() with { Id = Guid.NewGuid(), UsbDeviceId = Dongle };
        Poll(other, Desk);

        Poll([other, WheelbaseRule()], Desk, Wheelbase).ShouldBeEmpty();
    }

    [Fact]
    public void Reset_NextPollIsBaseline()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        _trigger.Reset();

        Poll(rule, Desk, Wheelbase).ShouldBeEmpty();
    }

    [Fact]
    public void RuleWithoutDevice_IsIgnored()
    {
        // An unreleased build wrote rules without a device key.
        var rule = new AutomationRule { ProfileId = Rig };
        var empty = new AutomationRule { UsbDeviceId = string.Empty, ProfileId = Rig };
        Poll([rule, empty], Desk);

        Poll([rule, empty], Desk, Wheelbase).ShouldBeEmpty();
        AutomationTrigger.WatchedDeviceOf(empty).ShouldBeNull();
    }

    [Fact]
    public void DeviceOffForLessThanTheRuleDelay_DoesNotSwitchBack()
    {
        AutomationRule rule = WheelbaseRule() with { ExitDelaySeconds = 60 };
        Poll(rule, Desk);
        Poll(rule, Desk, Wheelbase).ShouldHaveSingleItem();

        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(30);
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, Wheelbase).ShouldBeEmpty();

        GoneLongEnough(rule, Rig).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Desk, TriggerReason.Ended));
    }

    [Fact]
    public void ExitDelay_IsKeptWithinZeroAndTenMinutes()
    {
        AutomationTrigger.ExitDelayOf(new AutomationRule { ExitDelaySeconds = -5 }).ShouldBe(TimeSpan.Zero);
        AutomationTrigger.ExitDelayOf(new AutomationRule { ExitDelaySeconds = 99_999 }).ShouldBe(TimeSpan.FromMinutes(10));
        AutomationTrigger.ExitDelayOf(new AutomationRule()).ShouldBe(TimeSpan.FromSeconds(10));
    }

    private IReadOnlyList<TriggerAction> GoneLongEnough(AutomationRule rule, Guid active)
    {
        Poll(rule, active).ShouldBeEmpty();
        _now += AutomationTrigger.ExitDelayOf(rule);
        return Poll(rule, active);
    }
}
