using RigShift.Core.Automation;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class AutomationTriggerTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private static readonly Guid Desk = Guid.NewGuid();
    private static readonly Guid Rig = Guid.NewGuid();
    private static readonly Guid Tv = Guid.NewGuid();
    private static readonly DateTimeOffset Start = new(2026, 9, 14, 20, 0, 0, TimeSpan.Zero);

    private readonly AutomationTrigger _trigger = new();
    private DateTimeOffset _now = Start;

    private static AutomationRule IRacingRule(ExitAction onExit = ExitAction.SwitchBack, Guid? exitProfile = null, bool enabled = true, bool skip = false) => new()
    {
        TemplateId = "iracing",
        ProfileId = Rig,
        OnExit = onExit,
        ExitProfileId = exitProfile,
        IsEnabled = enabled,
        SkipConfirmation = skip,
    };

    private static HashSet<string> Running(params string[] names) => new(names, ProcessNames.Comparer);

    private IReadOnlyList<TriggerAction> Poll(AutomationRule rule, Guid? active, params string[] running) =>
        Poll([rule], active, running);

    private IReadOnlyList<TriggerAction> Poll(IReadOnlyList<AutomationRule> rules, Guid? active, params string[] running)
    {
        IReadOnlyList<TriggerAction> actions = _trigger.Evaluate(rules, Running(running), active, _now);
        _now += TimeSpan.FromSeconds(2);
        return actions;
    }

    [Fact]
    public void GameStarts_SwitchesToRuleProfile()
    {
        AutomationRule rule = IRacingRule(skip: true);
        Poll(rule, Desk).ShouldBeEmpty();

        IReadOnlyList<TriggerAction> actions = Poll(rule, Desk, "iRacingSim64DX11");

        actions.ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Rig, TriggerReason.Started));
        actions[0].SkipConfirmation.ShouldBeTrue();
    }

    [Fact]
    public void GameRunningAtFirstPoll_DoesNotSwitch()
    {
        AutomationRule rule = IRacingRule();

        Poll(rule, Desk, "iRacingSim64DX11").ShouldBeEmpty();
        Poll(rule, Desk, "iRacingSim64DX11").ShouldBeEmpty();
    }

    [Fact]
    public void ProfileAlreadyActive_DoesNotSwitchAndNotBackOnExit()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Rig).ShouldBeEmpty();
        Poll(rule, Rig, "iRacingSim64DX11").ShouldBeEmpty();

        GameGoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void GameCloses_SwitchesBackAfterDelay()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Desk);
        Poll(rule, Desk, "iRacingSim64DX11");

        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(5);
        Poll(rule, Rig).ShouldBeEmpty();
        _now += TimeSpan.FromSeconds(5);

        Poll(rule, Rig).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Desk, TriggerReason.Ended));
        GameGoneLongEnough(rule, Desk).ShouldBeEmpty();
    }

    [Fact]
    public void GameRestartsWithinDelay_NeitherSwitchesBackNorAgain()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Desk);
        Poll(rule, Desk, "iRacingSim64DX11");
        Poll(rule, Rig);

        Poll(rule, Rig, "iRacingSim64DX11").ShouldBeEmpty();
        _now += TimeSpan.FromMinutes(1);
        Poll(rule, Rig, "iRacingSim64DX11").ShouldBeEmpty();

        GameGoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Desk);
    }

    [Fact]
    public void UserPickedAnotherProfileDuringGame_ExitDoesNothing()
    {
        AutomationRule rule = IRacingRule(ExitAction.SwitchTo, Desk);
        Poll(rule, Desk);
        Poll(rule, Desk, "iRacingSim64DX11");

        GameGoneLongEnough(rule, Tv).ShouldBeEmpty();
    }

    [Fact]
    public void ExitSwitchTo_SwitchesToChosenProfile()
    {
        AutomationRule rule = IRacingRule(ExitAction.SwitchTo, Tv);
        Poll(rule, Desk);
        Poll(rule, Desk, "iRacingSim64DX11");

        GameGoneLongEnough(rule, Rig).ShouldHaveSingleItem().ProfileId.ShouldBe(Tv);
    }

    [Fact]
    public void ExitStay_DoesNothing()
    {
        AutomationRule rule = IRacingRule(ExitAction.Stay);
        Poll(rule, Desk);
        Poll(rule, Desk, "iRacingSim64DX11");

        GameGoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void DisabledRule_DoesNothing()
    {
        AutomationRule rule = IRacingRule(enabled: false);
        Poll(rule, Desk);

        Poll(rule, Desk, "iRacingSim64DX11").ShouldBeEmpty();
        GameGoneLongEnough(rule, Rig).ShouldBeEmpty();
    }

    [Fact]
    public void AnyExecutableOfTemplate_CountsAsRunning()
    {
        var rule = new AutomationRule { TemplateId = "ams2", ProfileId = Rig };
        Poll(rule, Desk);

        Poll(rule, Desk, "ams2").ShouldHaveSingleItem();
    }

    [Fact]
    public void CustomProgram_MatchedByFileName()
    {
        var rule = new AutomationRule { ExecutablePath = @"""C:\Games\My Sim\MySim.EXE""", ProfileId = Rig };
        Poll(rule, Desk);

        Poll(rule, Desk, "mysim").ShouldHaveSingleItem();
    }

    [Fact]
    public void GameOfRuleChangedWhileNewGameRuns_DoesNotSwitch()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Desk, "AC2-Win64-Shipping");

        Poll(rule with { TemplateId = "acc" }, Desk, "AC2-Win64-Shipping").ShouldBeEmpty();
    }

    [Fact]
    public void RuleAddedWhileGameRuns_DoesNotSwitch()
    {
        AutomationRule other = IRacingRule() with { TemplateId = "acc" };
        Poll(other, Desk);

        Poll([other, IRacingRule()], Desk, "iRacingSim64DX11").ShouldBeEmpty();
    }

    [Fact]
    public void Reset_NextPollIsBaseline()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Desk);
        _trigger.Reset();

        Poll(rule, Desk, "iRacingSim64DX11").ShouldBeEmpty();
    }

    [Fact]
    public void UsbDeviceConnects_SwitchesToRuleProfile()
    {
        var rule = new AutomationRule { UsbDeviceId = Wheelbase, ProfileId = Rig };
        Poll(rule, Desk).ShouldBeEmpty();

        Poll(rule, Desk, UsbDeviceIds.Key(Wheelbase)).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Rig, TriggerReason.Started));
    }

    [Fact]
    public void UsbDeviceGone_SwitchesBackAfterDelay()
    {
        var rule = new AutomationRule { UsbDeviceId = Wheelbase, ProfileId = Rig, OnExit = ExitAction.SwitchBack };
        Poll(rule, Desk);
        Poll(rule, Desk, UsbDeviceIds.Key(Wheelbase));

        GameGoneLongEnough(rule, Rig).ShouldHaveSingleItem().ShouldBe(new TriggerAction(rule, Desk, TriggerReason.Ended));
    }

    [Fact]
    public void UsbRule_IgnoresGameOfSameRuleAndProcessNamedLikeDevice()
    {
        var rule = new AutomationRule { UsbDeviceId = Wheelbase, TemplateId = "iracing", ProfileId = Rig };
        Poll(rule, Desk);

        Poll(rule, Desk, "iRacingSim64DX11", Wheelbase).ShouldBeEmpty();
    }

    [Fact]
    public void RuleChangedFromGameToConnectedDevice_DoesNotSwitch()
    {
        AutomationRule rule = IRacingRule();
        Poll(rule, Desk, UsbDeviceIds.Key(Wheelbase));

        Poll(rule with { TemplateId = null, UsbDeviceId = Wheelbase }, Desk, UsbDeviceIds.Key(Wheelbase)).ShouldBeEmpty();
    }

    private IReadOnlyList<TriggerAction> GameGoneLongEnough(AutomationRule rule, Guid active)
    {
        Poll(rule, active).ShouldBeEmpty();
        _now += _trigger.ExitDelay;
        return Poll(rule, active);
    }
}
