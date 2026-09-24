using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The settings page's "back to the previous profile" hotkey: recorded like an editor's, saved at once.</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltB = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x42 };

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private readonly HotkeyService _hotkeys;
    private readonly AutomationService _automation;

    public SettingsViewModelTests()
    {
        _games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _games,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        _hotkeys = new HotkeyService(_host.Catalog, _games, _sessions, _host.Coordinator, _host.Settings, Logger.None, new FakeHotkeyRegistrar());
        _automation = new AutomationService(
            _host.Settings, _host.Catalog, _host.Coordinator, _host.Usb, _host.Fullscreen, _host.Session, TimeProvider.System, Logger.None);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ToggleHotkey_Recorded_IsSaved_AndClearingRemovesIt()
    {
        SettingsViewModel settings = Create();

        settings.RecordHotkey(CtrlAltB.Modifiers, CtrlAltB.VirtualKey);

        settings.ToggleHotkey.ShouldBe(CtrlAltB);
        await UntilAsync(() => _host.Settings.Current.ToggleHotkey == CtrlAltB);

        settings.ClearHotkey();

        settings.HasToggleHotkey.ShouldBeFalse();
        settings.ToggleHotkeyHint.ShouldBe(Loc.Instance["Settings_ToggleHotkeyHint"]);
        await UntilAsync(() => _host.Settings.Current.ToggleHotkey is null);
    }

    /// <summary>A refused autostart change says so, and the switch shows what Windows really has (v4 finding A-16).</summary>
    [Fact]
    public void StartWithWindows_Refused_ShowsTheErrorAndTheRealState()
    {
        _host.Settings.Autostart.When(a => a.SetEnabled(true)).Do(_ => throw new UnauthorizedAccessException("policy"));
        SettingsViewModel settings = Create();
        settings.Load();

        settings.StartWithWindows = true;

        settings.StartWithWindows.ShouldBeFalse();
        settings.ErrorMessage.ShouldBe(Loc.Instance["Settings_AutostartFailed"]);
    }

    [Fact]
    public async Task ToggleHotkey_HeldByAProfile_IsRefusedAndNotSaved()
    {
        await _host.Catalog.SaveAsync(Rig() with { Hotkey = CtrlAltR }, Ct);
        SettingsViewModel settings = Create();

        settings.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);

        settings.ToggleHotkey.ShouldBeNull();
        settings.ToggleHotkeyHint.ShouldContain("Rig");
        _host.Settings.Current.ToggleHotkey.ShouldBeNull();
    }

    public void Dispose()
    {
        _automation.Dispose();
        _hotkeys.Dispose();
        _sessions.Dispose();
        _host.Dispose();
    }

    private SettingsViewModel Create()
    {
        var devices = new UsbDevicesViewModel(_host.Settings, _host.Catalog, _automation, _host.Usb, Logger.None);
        var settings = new SettingsViewModel(_host.Settings, _host.Catalog, _hotkeys, devices, Substitute.For<IUpdatePolicy>(), Logger.None);
        settings.Load();
        return settings;
    }

    /// <summary>The page saves in the background, and a save reaches the disk before it counts.</summary>
    private static async Task UntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100 && !condition(); attempt++)
        {
            await Task.Delay(20, Ct);
        }

        condition().ShouldBeTrue();
    }
}
