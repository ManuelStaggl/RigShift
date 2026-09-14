using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class AutomationTriggerTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";
    private static readonly string WheelbaseKey = UsbDeviceIds.Key(Wheelbase);
    private static readonly Guid Desk = Guid.NewGuid();
    private static readonly Guid Rig = Guid.NewGuid();
    private static readonly Guid Tv = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private readonly AutomationTrigger _trigger = new();
    private DateTimeOffset _now = Start;

    private static AutomationRule WheelbaseRule(ExitAction onExit = ExitAction.SwitchBack, Guid? exitProfile = null, bool enabled = true, bool skip = false) => new()
    {
        UsbDeviceId = Wheelbase,
        ProfileId = Rig,
        OnExit = onExit,
        ExitProfileId = exitProfile,
        IsEnabled = enabled,
        SkipConfirmation = skip,
    };

    private static HashSet<string> Present(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<TriggerAction> Poll(AutomationRule rule, Guid? active, params string[] present) =>
        Poll([rule], active, present);

    private IReadOnlyList<TriggerAction> Poll(IReadOnlyList<AutomationRule> rules, Guid? active, params string[] present)
    {
        IReadOnlyList<TriggerAction> actions = _trigger.Evaluate(rules, Present(present), active, _now);
        _now += TimeSpan.FromSeconds(2);
        return actions;
    }

    [Fact]
    public void DeviceConnects_SwitchesToRuleProfile()
    {
        AutomationRule rule = WheelbaseRule(skip: true);
        Poll(rule, Desk).ShouldBeEmpty();

        IReadOnlyList<TriggerAction> actions = Poll(rule, Desk, WheelbaseKey);

        actions.ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Rig, TriggerReason.Started));
        actions[0].SkipConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void DeviceConnectedAtFirstPoll_DoesNotSwitch()
    {
        AutomationRule rule = WheelbaseRule();

        Poll(rule, Desk, WheelbaseKey).ShouldBeEmpty();
        Poll(rule, Desk, WheelbaseKey).ShouldBeEmpty();
    }

    [Fact]
    public void ProfileAlreadyActive_DoesNotSwitchAndNotBackOnExit()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, WheelbaseKey).ShouldBeEmpty();

        GoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceGone_SwitchesBackAfterDelay()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        Poll(rule, Desk, WheelbaseKey);

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
        Poll(rule, Desk, WheelbaseKey);
        Poll(rule, Rig);

        Poll(rule, Rig, WheelbaseKey).ShouldBeEmpty();
        _now += TimeSpan.FromMinutes(1);
        Poll(rule, Rig, WheelbaseKey).ShouldBeEmpty();

        GoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Desk);
    }

    [Fact]
    public void UserPickedAnotherProfileMeanwhile_ExitDoesNothing()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.SwitchTo, Desk);
        Poll(rule, Desk);
        Poll(rule, Desk, WheelbaseKey);

        GoneLongEnough(rule, Tv).ShouldBeEmpty();
    }

    [Fact]
    public void ExitSwitchTo_SwitchesToChosenProfile()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.SwitchTo, Tv);
        Poll(rule, Desk);
        Poll(rule, Desk, WheelbaseKey);

        GoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Tv);
    }

    [Fact]
    public void ExitStay_DoesNothing()
    {
        AutomationRule rule = WheelbaseRule(ExitAction.Stay);
        Poll(rule, Desk);
        Poll(rule, Desk, WheelbaseKey);

        GoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DisabledRule_DoesNothing()
    {
        AutomationRule rule = WheelbaseRule(enabled: false);
        Poll(rule, Desk);

        Poll(rule, Desk, WheelbaseKey).ShouldBeEmpty();
        GoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceMatchedByVendorAndProductIdOnly()
    {
        var rule = new AutomationRule { UsbDeviceId = @"USB\VID_0eb7&PID_0020\5&1a2b", ProfileId = Rig };
        Poll(rule, Desk);

        Poll(rule, Desk, WheelbaseKey).ShouldHaveSingleItem();
    }

    [Fact]
    public void DeviceOfRuleChangedWhileNewDeviceIsConnected_DoesNotSwitch()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk, UsbDeviceIds.Key(Dongle));

        Poll(rule with { UsbDeviceId = Dongle }, Desk, UsbDeviceIds.Key(Dongle)).ShouldBeEmpty();
    }

    [Fact]
    public void RuleAddedWhileDeviceIsConnected_DoesNotSwitch()
    {
        AutomationRule other = WheelbaseRule() with { Id = Guid.NewGuid(), UsbDeviceId = Dongle };
        Poll(other, Desk);

        Poll([other, WheelbaseRule()], Desk, WheelbaseKey).ShouldBeEmpty();
    }

    [Fact]
    public void Reset_NextPollIsBaseline()
    {
        AutomationRule rule = WheelbaseRule();
        Poll(rule, Desk);
        _trigger.Reset();

        Poll(rule, Desk, WheelbaseKey).ShouldBeEmpty();
    }

    [Fact]
    public void RuleWithoutDevice_IsIgnored()
    {
        // Game rules written by an unreleased build have no device id.
        var rule = new AutomationRule { ProfileId = Rig };
        var empty = new AutomationRule { UsbDeviceId = string.Empty, ProfileId = Rig };
        Poll([rule, empty], Desk);

        Poll([rule, empty], Desk, WheelbaseKey, "iRacingSim64DX11").ShouldBeEmpty();
        AutomationTrigger.WatchedKeysOf(rule).ShouldBeEmpty();
    }

    [Fact]
    public void DeviceOffForLessThanTheRuleDelay_DoesNotSwitchBack()
    {
        AutomationRule rule = WheelbaseRule() with { ExitDelaySeconds = 60 };
        Poll(rule, Desk);
        Poll(rule, Desk, WheelbaseKey).ShouldHaveSingleItem();

        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(30);
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, WheelbaseKey).ShouldBeEmpty();

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
