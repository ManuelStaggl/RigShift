using NSubstitute;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class AutomationViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0020";
    private const string Dongle = "VID_046D&PID_C547";

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly Profile _rig = Rig();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_TwoRulesForSameDevice_BothCardsWarn()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase), RuleFor(Dongle));

        viewModel.Rules.Select(r => r.HasDuplicateDevice).ShouldBe([true, true, false]);
    }

    [Fact]
    public async Task ChangingDeviceOfDuplicate_ClearsWarningOnBoth()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase));

        viewModel.Rules[1].SelectedDevice = viewModel.DeviceChoiceFor(Dongle);

        viewModel.Rules.ShouldAllBe(r => !r.HasDuplicateDevice);
    }

    [Fact]
    public async Task DeletingDuplicate_ClearsWarning()
    {
        AutomationViewModel viewModel = await CreateAsync(RuleFor(Wheelbase), RuleFor(Wheelbase));

        await viewModel.DeleteRuleCommand.ExecuteAsync(viewModel.Rules[1]);

        viewModel.Rules.ShouldHaveSingleItem().HasDuplicateDevice.ShouldBeFalse();
    }

    public void Dispose() => _host.Dispose();

    private AutomationRule RuleFor(string device) => new() { UsbDeviceId = device, ProfileId = _rig.Id };

    private async Task<AutomationViewModel> CreateAsync(params AutomationRule[] rules)
    {
        _host.Usb.PresentDeviceIds().Returns(new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _host.Usb.ConnectedDevices().Returns([new UsbDevice(Wheelbase, "Wheelbase"), new UsbDevice(Dongle, "Dongle")]);
        _host.Store.Profiles.Add(_rig);
        await _host.Catalog.ReloadAsync(Ct);
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = rules }, Ct);

        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        var automation = new AutomationService(_host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, TimeProvider.System, Logger.None);
        var viewModel = new AutomationViewModel(_host.Settings, _host.Catalog, automation, _host.Usb, powerCheck, Logger.None);
        viewModel.Load();
        return viewModel;
    }
}
