using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The tray's context menu without a tray icon: what it offers, in which order, and in which state.</summary>
public sealed class TrayMenuModelTests
{
    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };

    [Fact]
    public void NothingConfigured_OffersTheCommandsAndSaysThereAreNoProfiles()
    {
        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(new TrayMenuState());

        Commands(menu).ShouldBe(
        [
            TrayMenuCommand.None, TrayMenuCommand.None, TrayMenuCommand.SaveCurrent, TrayMenuCommand.Open, TrayMenuCommand.Settings,
            TrayMenuCommand.None, TrayMenuCommand.Exit,
        ]);
        TrayMenuEntry noProfiles = menu[0];
        noProfiles.IsSeparator.ShouldBeFalse();
        noProfiles.Header.ShouldBe(Loc.Instance["Tray_NoProfiles"]);
        noProfiles.IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void Profiles_ComeFirst_TheActiveOneChecked_WithTheirHotkeys()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig() with { Hotkey = CtrlAltR };

        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(new TrayMenuState
        {
            Profiles = [new ProfileItem(desk), new ProfileItem(rig) { IsActive = true }],
        });

        menu[0].ShouldBe(new TrayMenuEntry(TrayMenuCommand.SwitchProfile, "Desk") { Profile = desk });
        menu[1].ShouldBe(new TrayMenuEntry(TrayMenuCommand.SwitchProfile, "Rig")
        {
            Profile = rig,
            IsChecked = true,
            Gesture = HotkeyFormat.Format(CtrlAltR),
        });
        menu[2].IsSeparator.ShouldBeTrue();
        menu.ShouldNotContain(e => e.Header == Loc.Instance["Tray_NoProfiles"]);
    }

    [Fact]
    public void Games_FollowTheProfilesAfterALine_ARunningOneCannotBeStartedAgain()
    {
        GameEntry iracing = Game("iRacing") with { Hotkey = CtrlAltR };
        GameEntry ac = Game("Assetto Corsa");

        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(new TrayMenuState
        {
            Profiles = [new ProfileItem(Rig())],
            Games = [new GameItem(iracing), new GameItem(ac)],
            RunningGames = new HashSet<Guid> { ac.Id },
        });

        Commands(menu)[..4].ShouldBe([TrayMenuCommand.SwitchProfile, TrayMenuCommand.None, TrayMenuCommand.PlayGame, TrayMenuCommand.PlayGame]);
        menu[1].IsSeparator.ShouldBeTrue();
        menu[2].ShouldBe(new TrayMenuEntry(TrayMenuCommand.PlayGame, Loc.Format("Tray_PlayGame", "iRacing"))
        {
            Game = iracing,
            Gesture = HotkeyFormat.Format(CtrlAltR),
        });
        menu[3].Game.ShouldBe(ac);
        menu[3].IsEnabled.ShouldBeFalse();
    }

    [Fact]
    public void PauseLine_OnlyWithUsbRules_CheckedWhilePaused()
    {
        TrayMenuModel.Build(new TrayMenuState { AutomationPaused = true })
            .ShouldNotContain(e => e.Command == TrayMenuCommand.PauseAutomation);

        TrayMenuEntry pause = TrayMenuModel.Build(new TrayMenuState { HasAutomation = true, AutomationPaused = true })
            .Single(e => e.Command == TrayMenuCommand.PauseAutomation);

        pause.IsCheckable.ShouldBeTrue();
        pause.IsChecked.ShouldBeTrue();
    }

    [Fact]
    public void UpdateLine_OnlyWithAVersion_RightBeforeExit_DisabledWhileItCannotInstall()
    {
        TrayMenuModel.Build(new TrayMenuState { CanInstallUpdate = true })
            .ShouldNotContain(e => e.Command == TrayMenuCommand.InstallUpdate);

        IReadOnlyList<TrayMenuEntry> menu = TrayMenuModel.Build(new TrayMenuState { UpdateVersion = "4.0.1", CanInstallUpdate = false });

        menu[^2].Command.ShouldBe(TrayMenuCommand.InstallUpdate);
        menu[^2].Header.ShouldBe(Loc.Format("Tray_RestartToUpdate", "4.0.1"));
        menu[^2].IsEnabled.ShouldBeFalse();
        menu[^1].Command.ShouldBe(TrayMenuCommand.Exit);
    }

    private static TrayMenuCommand[] Commands(IReadOnlyList<TrayMenuEntry> menu) => [.. menu.Select(e => e.Command)];

    private static GameEntry Game(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
    };
}
