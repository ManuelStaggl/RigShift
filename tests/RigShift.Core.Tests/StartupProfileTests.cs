using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class StartupProfileTests
{
    private const string Wheelbase = "VID_0EB7&PID_0020";

    private static readonly HashSet<string> Nothing = [];

    private readonly Profile _desk = Profile("Desk", DeskModes);
    private readonly Profile _rig = Rig();

    private AppSettings On(AutomationRule? rule = null, bool paused = false) => new()
    {
        DefaultProfileId = _desk.Id,
        ApplyDefaultProfileWithWindows = true,
        AutomationRules = rule is null ? null : [rule],
        AutomationPaused = paused,
    };

    private AutomationRule WheelbaseRule => new() { Devices = [new RuleDevice { Id = Wheelbase }], ProfileId = _rig.Id };

    [Fact]
    public void Choose_RigLeftOverFromShutdown_AppliesTheDefault()
    {
        var (decision, profile) = StartupProfile.Choose(On(), [_desk, _rig], _rig.Id, Nothing);

        decision.ShouldBe(StartupProfileDecision.Apply);
        profile.ShouldBe(_desk);
    }

    [Fact]
    public void Choose_SettingOff_DoesNothing()
    {
        StartupProfile.Choose(On() with { ApplyDefaultProfileWithWindows = false }, [_desk, _rig], _rig.Id, Nothing)
            .Decision.ShouldBe(StartupProfileDecision.TurnedOff);
    }

    [Fact]
    public void Choose_DefaultProfileMissing_DoesNothing()
    {
        StartupProfile.Choose(On() with { DefaultProfileId = Guid.NewGuid() }, [_desk, _rig], _rig.Id, Nothing)
            .Decision.ShouldBe(StartupProfileDecision.NoDefaultProfile);
    }

    [Fact]
    public void Choose_DefaultAlreadyActive_DoesNothing()
    {
        StartupProfile.Choose(On(), [_desk, _rig], _desk.Id, Nothing).Decision.ShouldBe(StartupProfileDecision.AlreadyActive);
    }

    [Fact]
    public void Choose_RuleDevicesConnected_TheRuleWins()
    {
        StartupProfile.Choose(On(WheelbaseRule), [_desk, _rig], _rig.Id, new HashSet<string> { Wheelbase })
            .Decision.ShouldBe(StartupProfileDecision.RuleDevicesConnected);
    }

    [Fact]
    public void Choose_RuleDevicesConnectedButAutomationPaused_AppliesTheDefault()
    {
        StartupProfile.Choose(On(WheelbaseRule, paused: true), [_desk, _rig], _rig.Id, new HashSet<string> { Wheelbase })
            .Decision.ShouldBe(StartupProfileDecision.Apply);
    }

    [Fact]
    public void Choose_RuleDevicesNotConnected_AppliesTheDefault()
    {
        StartupProfile.Choose(On(WheelbaseRule), [_desk, _rig], _rig.Id, Nothing).Decision.ShouldBe(StartupProfileDecision.Apply);
    }
}
