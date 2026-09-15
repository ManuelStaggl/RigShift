using NSubstitute;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

#pragma warning disable xUnit1051 // NSubstitute matchers stand in for cancellation tokens.

namespace RigShift.Core.Tests;

public sealed class CommandRunnerTests
{
    private static readonly AudioEndpoint Headphones = new("{0.0.0.00000000}.{test-1}", "Headphones");

    private readonly InMemoryProfileStore _store = new();
    private readonly IAudioController _audio = Substitute.For<IAudioController>();
    private readonly IProfileSwitcher _switcher = Substitute.For<IProfileSwitcher>();
    private readonly Profile _desk = Profile("Desk", DeskModes);

    public CommandRunnerTests()
    {
        _store.Profiles.AddRange([_desk, Rig()]);
        _audio.ListAsync(AudioDirection.Render, Arg.Any<CancellationToken>())
            .Returns([new AudioDeviceInfo(Headphones, AudioDirection.Render, IsActive: true, IsDefault: true)]);
    }

    [Fact]
    public async Task List_MarksTheActiveProfile()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.List }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe($"* Desk{Environment.NewLine}  Rig");
    }

    [Fact]
    public async Task List_WithoutDisplayAccess_StillListsProfiles()
    {
        IDisplayConfigurator display = Substitute.For<IDisplayConfigurator>();
        display.QueryAsync(Arg.Any<CancellationToken>())
            .Returns<DisplaySnapshot>(_ => throw new System.ComponentModel.Win32Exception(5));
        var runner = new CommandRunner(_store, display, _audio, Matcher(), Serilog.Core.Logger.None);

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.List }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldBe($"  Desk{Environment.NewLine}  Rig");
    }

    [Fact]
    public async Task Status_NamesActiveProfileAndDisplays()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Status }, CancellationToken.None);

        response.Output.ShouldContain("Active profile: Desk");
        response.Output.ShouldContain("Desk 4K: 3840x2160 @ 165 Hz at (0, 0), primary");
    }

    [Fact]
    public async Task Apply_UnknownProfile_Returns4()
    {
        CliResponse response = await Runner(_switcher).RunAsync(Apply("Couch"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.ProfileNotFound);
        response.Output.ShouldContain("Desk, Rig");
        await _switcher.DidNotReceiveWithAnyArgs().SwitchAsync(default!, default!, default);
    }

    [Theory]
    [InlineData(SwitchOutcome.Applied, CliExitCodes.Applied)]
    [InlineData(SwitchOutcome.AppliedPartially, CliExitCodes.Applied)]
    [InlineData(SwitchOutcome.Failed, CliExitCodes.Failed)]
    [InlineData(SwitchOutcome.Blocked, CliExitCodes.Blocked)]
    [InlineData(SwitchOutcome.RolledBack, CliExitCodes.RolledBack)]
    public async Task Apply_MapsOutcomeToExitCode(SwitchOutcome outcome, int exitCode)
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new SwitchResult { Outcome = outcome, Plan = EmptyPlan(call.Arg<Profile>()) });

        CliResponse response = await Runner(_switcher).RunAsync(Apply("rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(exitCode);
    }

    [Fact]
    public async Task Apply_PassesNoConfirmAndDryRun()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => new SwitchResult { Outcome = SwitchOutcome.DryRun, Plan = EmptyPlan(call.Arg<Profile>()) });

        CliResponse response = await Runner(_switcher).RunAsync(
            Apply("Rig") with { NoConfirm = true, DryRun = true, FromLink = true }, CancellationToken.None);

        response.Output.ShouldBe("Rig: ready");
        await _switcher.Received(1).SwitchAsync(
            Arg.Is<Profile>(p => p.Name == "Rig"),
            Arg.Is<SwitchRequest>(r => r.SkipConfirmation && r.DryRun && r.FromLink),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_WhileBusy_Fails()
    {
        _switcher.SwitchAsync(Arg.Any<Profile>(), Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>()).Returns((SwitchResult?)null);

        CliResponse response = await Runner(_switcher).RunAsync(Apply("Rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("Another switch");
    }

    [Fact]
    public async Task Apply_WithoutApp_Fails()
    {
        CliResponse response = await Runner().RunAsync(Apply("Rig"), CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
    }

    [Fact]
    public async Task Toggle_WithoutTarget_Returns4()
    {
        _switcher.ToggleTarget.Returns((Profile?)null);

        CliResponse response = await Runner(_switcher).RunAsync(new CliRequest { Command = CliCommand.Toggle }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.ProfileNotFound);
        response.Output.ShouldContain("No previous profile");
    }

    [Fact]
    public async Task Toggle_SwitchesToTheTargetWithTheOptions()
    {
        Profile rig = Rig();
        _switcher.ToggleTarget.Returns(rig);
        _switcher.SwitchAsync(rig, Arg.Any<SwitchRequest>(), Arg.Any<CancellationToken>())
            .Returns(new SwitchResult { Outcome = SwitchOutcome.Applied, Plan = EmptyPlan(rig) });

        CliResponse response = await Runner(_switcher)
            .RunAsync(new CliRequest { Command = CliCommand.Toggle, NoConfirm = true, FromLink = true }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        response.Output.ShouldStartWith("Rig: Applied");
        await _switcher.Received().SwitchAsync(rig, Arg.Is<SwitchRequest>(r => r.SkipConfirmation && r.FromLink && !r.DryRun), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Toggle_WithoutApp_Fails()
    {
        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Toggle }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
    }

    [Fact]
    public async Task Save_NewName_CapturesDisplaysAndDefaultPlayback()
    {
        CommandRunner runner = Runner();
        bool changed = false;
        runner.ProfilesChanged += (_, _) => changed = true;

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Desk copy" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        changed.ShouldBeTrue();
        Profile saved = _store.Profiles.Single(p => p.Name == "Desk copy");
        saved.Displays.Count.ShouldBe(3);
        saved.Audio.Playback.ShouldBe(Headphones);
    }

    [Fact]
    public async Task Save_ExistingName_UpdatesArrangementAndKeepsIdentity()
    {
        Profile rig = _store.Profiles.Single(p => p.Name == "Rig") with { Icon = "rig", SwitchWithoutAsking = true };
        _store.Profiles[1] = rig;

        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "RIG" }, CancellationToken.None);

        response.Output.ShouldStartWith("Updated profile 'Rig'");
        Profile saved = _store.Profiles.Single(p => p.Id == rig.Id);
        saved.Icon.ShouldBe("rig");
        saved.SwitchWithoutAsking.ShouldBeTrue();
        saved.Displays.Select(d => d.Identity).ShouldBe([DeskLeft, Desk4K, DeskRight]);
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_NoActiveDisplay_Fails()
    {
        var runner = new CommandRunner(_store, new FakeDisplayConfigurator(Snapshot()), _audio, Matcher(), Serilog.Core.Logger.None);

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Empty" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_WhenLoadIncomplete_DoesNotCreateDuplicate()
    {
        _store.Unreadable.Add(new UnreadableProfileFile("locked.json", "The process cannot access the file."));
        CommandRunner runner = Runner();
        bool changed = false;
        runner.ProfilesChanged += (_, _) => changed = true;

        CliResponse response = await runner.RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Wheel" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Failed);
        response.Output.ShouldContain("locked.json");
        changed.ShouldBeFalse();
        _store.Profiles.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Save_WhenLoadIncomplete_StillUpdatesAFoundProfile()
    {
        _store.Unreadable.Add(new UnreadableProfileFile("locked.json", "The process cannot access the file."));

        CliResponse response = await Runner().RunAsync(new CliRequest { Command = CliCommand.Save, ProfileName = "Rig" }, CancellationToken.None);

        response.ExitCode.ShouldBe(CliExitCodes.Applied);
        _store.Profiles.Count.ShouldBe(2);
    }

    private static CliRequest Apply(string name) => new() { Command = CliCommand.Apply, ProfileName = name };

    private static TopologyPlan EmptyPlan(Profile profile) => new() { Profile = profile, Resolved = [], Missing = [], Warnings = [] };

    private static ActiveProfileMatcher Matcher() => new(new TopologyPlanner(new TopologyPlannerOptions()));

    private CommandRunner Runner(IProfileSwitcher? switcher = null) =>
        new(_store, new FakeDisplayConfigurator(DeskActive()), _audio, Matcher(), Serilog.Core.Logger.None, switcher);
}
