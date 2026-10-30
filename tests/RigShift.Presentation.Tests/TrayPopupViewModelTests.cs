using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The tray popup without a window: what its rows say, and what a running switch does to them.</summary>
public sealed class TrayPopupViewModelTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly InMemoryGameStore _games = new();
    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;

    public TrayPopupViewModelTests()
    {
        _catalog = new GameCatalog(_games, _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _catalog,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
    }

    [Fact]
    public async Task SwitchingText_NamesTheProfileTheSwitchGoesTo()
    {
        TrayPopupViewModel popup = await PopupAsync();

        _host.Coordinator.SwitchingProfile = _host.Catalog.Profiles.Single(p => p.Name == "Sim Rig");
        _host.Coordinator.IsSwitching = true;

        popup.SwitchingText.ShouldContain("Sim Rig");
    }

    [Fact]
    public async Task SwitchingText_WithoutAKnownTarget_IsThePlainSentence()
    {
        TrayPopupViewModel popup = await PopupAsync();

        popup.SwitchingText.ShouldBe(Loc.Instance["Tray_Switching"]);
    }

    [Fact]
    public async Task StatusText_OnlyStandsInForAnEmptyList()
    {
        TrayPopupViewModel popup = await PopupAsync();
        popup.HasStatusText.ShouldBeFalse();

        foreach (Profile profile in _host.Catalog.Profiles.ToList())
        {
            await _host.Catalog.DeleteAsync(profile, CancellationToken.None);
        }

        popup.StatusText.ShouldBe(Loc.Instance["Tray_NoProfiles"]);
    }

    [Fact]
    public async Task GameRow_ShowsItsHotkeyOnlyWhileItIsNotRunning()
    {
        _games.Games.Add(new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "iRacing",
            Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
            Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x74 },
        });

        TrayPopupViewModel popup = await PopupAsync();
        GameItem item = popup.Games.Items.ShouldHaveSingleItem();

        item.PlayText.ShouldContain("iRacing");
        item.HotkeyText.ShouldNotBeEmpty();
        item.ShowsHotkey.ShouldBeTrue();

        item.IsRunning = true;
        item.ShowsHotkey.ShouldBeFalse();
    }

    public void Dispose()
    {
        _sessions.Dispose();
        _host.Dispose();
    }

    private async Task<TrayPopupViewModel> PopupAsync()
    {
        await _host.Catalog.SaveAsync(new Profile { Id = Guid.NewGuid(), Name = "Desk", Displays = [] }, CancellationToken.None);
        await _host.Catalog.SaveAsync(new Profile { Id = Guid.NewGuid(), Name = "Sim Rig", Displays = [] }, CancellationToken.None);
        await _host.Catalog.ReloadAsync(CancellationToken.None);
        await _catalog.ReloadAsync(CancellationToken.None);
        return new TrayPopupViewModel(
            _host.Catalog,
            _catalog,
            _sessions,
            _host.Coordinator,
            () => throw new InvalidOperationException("the profiles page is not built in these tests"),
            Substitute.For<IAppShell>());
    }
}
