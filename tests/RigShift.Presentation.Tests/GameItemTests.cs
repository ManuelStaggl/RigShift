using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>A game's row in the list and in the tray popup.</summary>
public sealed class GameItemTests
{
    private static readonly GameEntry Lmu = new()
    {
        Id = Guid.NewGuid(),
        Name = "LMU",
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "2399420" },
    };

    [Theory]
    [InlineData(GameLaunchKind.Steam, "2399420", "Steam · Rig")]
    [InlineData(GameLaunchKind.Epic, "abc", "Epic · Rig")]
    [InlineData(GameLaunchKind.Xbox, "Forza", "Xbox · Rig")]
    [InlineData(GameLaunchKind.Executable, @"C:\Games\ac2.exe", "ac2.exe · Rig")]
    public void SourceText_NamesWhereTheGameComesFrom_AndTheProfile(GameLaunchKind kind, string target, string expected)
    {
        var item = new GameItem(Lmu with { Launch = new GameLaunch { Kind = kind, Target = target } }, "Rig");

        item.SourceText.ShouldBe(expected);
        new GameItem(Lmu).SourceText.ShouldBe("Steam");
    }

    [Fact]
    public void Running_HidesTheHotkey_LocksPlay_AndIsAnnounced()
    {
        var item = new GameItem(Lmu with { Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x74 } });
        var changed = new List<string?>();
        item.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        item.ShowsHotkey.ShouldBeTrue();
        item.HotkeyText.ShouldNotBeEmpty();
        item.CanPlay.ShouldBeTrue();
        item.AccessibleName.ShouldBe("LMU");
        item.PlayText.ShouldBe(Loc.Format("Tray_PlayGame", "LMU"));

        item.IsRunning = true;

        item.ShowsHotkey.ShouldBeFalse();
        item.CanPlay.ShouldBeFalse();
        item.AccessibleName.ShouldBe($"LMU, {Loc.Instance["Game_Running"]}");
        changed.ShouldContain(nameof(GameItem.CanPlay));
        changed.ShouldContain(nameof(GameItem.ShowsHotkey));
    }

    [Fact]
    public void NewGame_CannotBePlayed_AndHasNoHotkeyText()
    {
        var item = new GameItem(Lmu) { IsNew = true };

        item.CanPlay.ShouldBeFalse();
        item.HotkeyText.ShouldBeEmpty();
        item.ShowsHotkey.ShouldBeFalse();
    }

    [Fact]
    public void Icons_ASymbolWinsOverTheGamesIcon_AndWithoutEitherTheRowGetsAFallback()
    {
        var plain = new GameItem(Lmu);
        plain.HasIcon.ShouldBeFalse();
        plain.HasFallbackIcon.ShouldBeTrue();

        plain.GameIcon = new object();
        plain.HasGameIcon.ShouldBeTrue();
        plain.HasFallbackIcon.ShouldBeFalse();

        var symbol = new GameItem(Lmu with { Icon = "wheel" }) { GameIcon = new object() };
        symbol.HasGameIcon.ShouldBe(symbol.IconKey is null);
    }

    [Fact]
    public void AppsAndWindows_AreSummedUpInOneLineEach()
    {
        var item = new GameItem(Lmu with
        {
            Apps = [new AppAction { Path = @"C:\SimHub\SimHubWPF.exe", Name = "SimHub" }, new AppAction { Path = @"C:\Tools\crewchief.exe" }],
            WindowLayout = new WindowLayout { Windows = [Window("SimHub"), Window("crewchief")] },
        });

        item.AppsLine.ShouldBe("SimHub, crewchief");
        item.HasApps.ShouldBeTrue();
        item.WindowsLine.ShouldBe(Loc.Format("Game_WindowsSaved", 2));
        item.HasWindows.ShouldBeTrue();
        item.AppIcons.ShouldBeEmpty("no program is installed at those paths");

        var single = new GameItem(Lmu with { WindowLayout = new WindowLayout { Windows = [Window("SimHub")] } });
        single.WindowsLine.ShouldBe(Loc.Instance["Game_WindowsSavedOne"]);
        single.HasApps.ShouldBeFalse();
        new GameItem(Lmu).HasWindows.ShouldBeFalse();
    }

    [Fact]
    public void SetStatus_ShowsUnderTheName()
    {
        var item = new GameItem(Lmu);

        item.SetStatus(StatusKind.Error, "failed");

        item.ListKind.ShouldBe(StatusKind.Error);
        item.ListStatus.ShouldBe("failed");
    }

    private static WindowPlacement Window(string process) =>
        new() { ProcessName = process, Title = process, Bounds = new PixelRect(0, 0, 800, 600) };
}
