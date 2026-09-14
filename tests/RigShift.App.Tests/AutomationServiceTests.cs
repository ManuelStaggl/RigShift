using System.IO;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
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

        // A directory where the temporary settings file goes makes the save fail.
        Directory.CreateDirectory(_host.Paths.SettingsFile + ".tmp");

        (await automation.SetPausedAsync(true)).ShouldBeFalse();
        automation.IsPaused.ShouldBeFalse();
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
            s => s with { AutomationRules = [new AutomationRule { UsbDeviceId = Wheelbase, ProfileId = _rig.Id }] }, Ct);
        return new AutomationService(_host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, TimeProvider.System, Logger.None);
    }
}
