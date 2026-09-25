using System.IO;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class AutomationServiceTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0020";

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly Profile _rig = Rig();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>E-08: without a rule that can act, the timer only woke the UI thread every two seconds for nothing.</summary>
    [Fact]
    public async Task Timer_RunsOnlyWhileARuleCanAct()
    {
        using var ui = new DispatcherThread();
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = [] }, Ct);
        AutomationService automation = ui.Invoke(() => new AutomationService(
            _host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, _host.Fullscreen, _host.Session, TimeProvider.System, Logger.None));
        try
        {
            ui.Invoke(automation.Start);
            ui.Invoke(() => automation.IsPolling).ShouldBeFalse();

            await _host.Settings.UpdateAsync(
                s => s with { AutomationRules = [new AutomationRule { Devices = [new RuleDevice { Id = Wheelbase }], ProfileId = _rig.Id }] }, Ct);
            ui.Drain();
            ui.Invoke(() => automation.IsPolling).ShouldBeTrue();

            (await automation.SetPausedAsync(true)).ShouldBeTrue();
            ui.Drain();
            ui.Invoke(() => automation.IsPolling).ShouldBeFalse();
        }
        finally
        {
            ui.Invoke(automation.Dispose);
        }
    }

    [Fact]
    public async Task Tick_WhileSwitching_EvaluatesNothing()
    {
        using AutomationService automation = await CreateAsync();
        await automation.PollAsync();

        Connected(true);
        _host.Coordinator.IsSwitching = true;
        await automation.PollAsync();
        _host.Coordinator.IsSwitching = false;

        // Had the skipped poll been evaluated, the device would count as known and this poll would not start the rig.
        await automation.PollAsync();
        _host.Coordinator.History.ShouldHaveSingleItem().ProfileName.ShouldBe("Rig");
    }

    [Fact]
    public async Task Tick_WhileLocked_DoesNothing_AndTheUnlockStartsTheRule()
    {
        // A-04: the driver sits down in the rig, switches the wheelbase on, then unlocks. While locked every display call
        // fails with "access denied", and the failed start used to disarm the rule until the wheelbase was switched again.
        using AutomationService automation = await CreateAsync();
        await automation.PollAsync();

        _host.Session.IsInteractive = false;
        Connected(true);
        await automation.PollAsync();
        _host.Coordinator.History.ShouldBeEmpty();

        _host.Session.IsInteractive = true;
        await automation.PollAsync();

        _host.Coordinator.History.ShouldHaveSingleItem().ProfileName.ShouldBe("Rig");
    }

    [Fact]
    public async Task Tick_RuleSwitch_CountsDownAtLeastTheRuleMinimum()
    {
        // U-04: the wheelbase is on, its owner may still be on the way to the seat; the app setting says 15 s.
        _host.Confirmation.ConfirmAsync(Arg.Any<Profile>(), Arg.Any<DisplaySnapshot>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(ConfirmationResult.Confirmed);
        using AutomationService automation = await CreateAsync();
        _host.Store.Profiles[0] = _rig with { SwitchWithoutAsking = false };
        await _host.Catalog.ReloadAsync(Ct);
        await automation.PollAsync();

        Connected(true);
        await automation.PollAsync();

        await _host.Confirmation.Received(1).ConfirmAsync(
            Arg.Any<Profile>(),
            Arg.Any<DisplaySnapshot>(),
            TimeSpan.FromSeconds(AutomationService.RuleConfirmSeconds),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Tick_Paused_ResetsBaselineOnce()
    {
        using AutomationService automation = await CreateAsync();
        await automation.PollAsync();

        await automation.SetPausedAsync(true);
        await automation.PollAsync();
        await automation.PollAsync();
        Connected(true);
        await automation.SetPausedAsync(false);

        // The device connected while paused: the first poll after resuming is a baseline, not a start.
        await automation.PollAsync();
        await automation.PollAsync();
        _host.Coordinator.History.ShouldBeEmpty();
    }

    [Fact]
    public async Task Tick_StartBlocked_RuleDisarmedAndRetriedLater()
    {
        _host.Display.SetSnapshot(Snapshot(Attached(Desk4K, activeMode: DeskModes[0])));
        using AutomationService automation = await CreateAsync();
        await automation.PollAsync();

        Connected(true);
        await automation.PollAsync();
        _host.Coordinator.History.ShouldHaveSingleItem().Outcome.ShouldBe(Core.Topology.SwitchOutcome.Blocked);

        // The ultrawide is attached now; the retry waits for the minimum delay, so the next poll does not switch yet.
        _host.Display.SetSnapshot(DeskActive());
        await automation.PollAsync();
        _host.Coordinator.History.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetPaused_SettingsNotWritable_ReturnsFalseAndStaysResumed()
    {
        using AutomationService automation = await CreateAsync();

        // A read-only settings file cannot be replaced, so the save fails.
        Directory.CreateDirectory(Path.GetDirectoryName(_host.Paths.SettingsFile)!);
        if (!File.Exists(_host.Paths.SettingsFile))
        {
            await File.WriteAllTextAsync(_host.Paths.SettingsFile, "{}", TestContext.Current.CancellationToken);
        }

        File.SetAttributes(_host.Paths.SettingsFile, FileAttributes.ReadOnly);
        try
        {
            (await automation.SetPausedAsync(true)).ShouldBeFalse();
            automation.IsPaused.ShouldBeFalse();
        }
        finally
        {
            File.SetAttributes(_host.Paths.SettingsFile, FileAttributes.Normal);
        }
    }

    public void Dispose() => _host.Dispose();

    private void Connected(bool connected) =>
        _host.Usb.PresentDeviceIds().Returns(connected
            ? new HashSet<string>([Wheelbase], StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private async Task<AutomationService> CreateAsync()
    {
        Connected(false);
        _host.Store.Profiles.Add(_rig);
        await _host.Catalog.ReloadAsync(Ct);
        await _host.Settings.UpdateAsync(
            s => s with { AutomationRules = [new AutomationRule { Devices = [new RuleDevice { Id = Wheelbase }], ProfileId = _rig.Id }] }, Ct);
        return new AutomationService(_host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, _host.Fullscreen, _host.Session, TimeProvider.System, Logger.None);
    }
}
