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
        SignInProfileId = _desk.Id,
        AutomationRules = rule is null ? null : [rule],
        AutomationPaused = paused,
    };

    private AutomationRule WheelbaseRule => new() { Devices = [new RuleDevice { Id = Wheelbase }], ProfileId = _rig.Id };

    [Fact]
    public void Choose_RigLeftOverFromShutdown_AppliesTheSignInProfile()
    {
        var (decision, profile) = StartupProfile.Choose(On(), [_desk, _rig], _rig.Id, Nothing);

        decision.ShouldBe(StartupProfileDecision.Apply);
        profile.ShouldBe(_desk);
    }

    [Fact]
    public void Choose_KeepWhatWindowsRestored_DoesNothing()
    {
        StartupProfile.Choose(On() with { SignInProfileId = null }, [_desk, _rig], _rig.Id, Nothing)
            .Decision.ShouldBe(StartupProfileDecision.TurnedOff);
    }

    [Fact]
    public void Choose_SignInProfileDeleted_DoesNothing()
    {
        StartupProfile.Choose(On() with { SignInProfileId = Guid.NewGuid() }, [_desk, _rig], _rig.Id, Nothing)
            .Decision.ShouldBe(StartupProfileDecision.ProfileMissing);
    }

    [Fact]
    public void Choose_SignInProfileAlreadyActive_DoesNothing()
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
    public void Choose_RuleDevicesConnectedButAutomationPaused_AppliesTheSignInProfile()
    {
        StartupProfile.Choose(On(WheelbaseRule, paused: true), [_desk, _rig], _rig.Id, new HashSet<string> { Wheelbase })
            .Decision.ShouldBe(StartupProfileDecision.Apply);
    }

    [Fact]
    public void Choose_RuleDevicesNotConnected_AppliesTheSignInProfile()
    {
        StartupProfile.Choose(On(WheelbaseRule), [_desk, _rig], _rig.Id, Nothing).Decision.ShouldBe(StartupProfileDecision.Apply);
    }
}
