using System.IO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The game editor without its page: what it shows for a stored game, what it validates, what it writes.</summary>
public sealed class GameEditorViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0006";

    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly InMemoryGameStore _store = new();
    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly HotkeyService _hotkeys;
    private readonly List<GameEditorViewModel> _editors = [];

    public GameEditorViewModelTests()
    {
        _catalog = new GameCatalog(_store, _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _catalog,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        _hotkeys = new HotkeyService(_host.Catalog, _catalog, _sessions, _host.Coordinator, _host.Settings, Logger.None, _registrar);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void StoredGame_OpensCleanAndBuildsItselfBack()
    {
        Profile rig = Rig();
        GameEntry game = Game("iRacing") with
        {
            Icon = ProfileIcons.All[0],
            ProfileId = rig.Id,
            Apps = [new AppAction { Path = @"C:\Tools\SimHub.exe", Name = "SimHub", WaitSeconds = 5, When = AppTiming.AfterGame }],
            AppsWaitForUsbDeviceId = Wheelbase,
            AppsWaitForUsbDeviceName = "Fanatec Wheelbase",
            EndsWith = SessionEnd.LauncherProcess,
            LauncherProcessName = "iRacingUI",
            Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = rig.Id, StopApps = false },
            Hotkey = CtrlAltR,
            StartWithGame = true,
        };

        GameEditorViewModel editor = Editor(game, profiles: [rig], games: [game]);

        editor.IsDirty.ShouldBeFalse();
        editor.ProblemCount.ShouldBe(0);
        editor.NeedsLauncherProcess.ShouldBeTrue();
        GameEntry built = editor.ToGame();
        built.Apps.ShouldBe(game.Apps);
        (built with { Apps = game.Apps }).ShouldBe(game);
    }

    [Fact]
    public void NewGame_IsDirtyBeforeAnythingIsTyped()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"), isNew: true);

        editor.IsDirty.ShouldBeTrue();
        editor.ProblemCount.ShouldBe(0);
    }

    [Fact]
    public void Change_MakesDirty_AndChangingBackMakesCleanAgain()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.Name = "iRacing 2";
        editor.IsDirty.ShouldBeTrue();

        editor.Name = " iRacing ";
        editor.IsDirty.ShouldBeFalse();
    }

    /// <summary>Every field counts for the save bar: a hand-kept list of names used to decide, and a field missing from it lost changes.</summary>
    [Fact]
    public void EveryField_MakesDirty()
    {
        Profile rig = Rig();
        GameEntry game = Game("iRacing");
        var windows = new WindowLayout
        {
            CapturedAt = DateTimeOffset.UtcNow,
            Windows = [new WindowPlacement { ProcessName = "SimHub", Title = "SimHub", Bounds = new PixelRect(0, 0, 800, 600) }],
        };
        Action<GameEditorViewModel>[] changes =
        [
            e => e.Name = "iRacing 2",
            e => e.LaunchTarget = "44690",
            e => e.ProcessName = "iRacingSim64DX11",
            e => e.LauncherProcessName = "iRacingUI",
            e => e.StartWithGame = true,
            e => e.StopApps = !e.StopApps,
            e => e.WindowLayout = windows,
            e => e.Hotkey = CtrlAltR,
            e => e.SelectedProfile = e.ProfileOptions.First(o => o.Key == rig.Id.ToString()),
            e => e.SelectedEnd = e.EndChoices.First(c => c.Key == nameof(SessionEnd.LauncherProcess)),
            e => e.SelectedExit = e.ExitChoices.First(c => c.Key == nameof(GameExitKind.PreviousProfile)),
            e => e.SelectedIcon = e.IconChoices.First(c => c.Key is not null),
        ];

        for (int i = 0; i < changes.Length; i++)
        {
            GameEditorViewModel editor = Editor(game, profiles: [rig], games: [game]);
            changes[i](editor);
            editor.IsDirty.ShouldBeTrue($"change {i}");
        }
    }

    /// <summary>The tab carries one dot for launch and hotkey; picking what starts has to clear it.</summary>
    [Fact]
    public void PickingWhatStarts_ClearsTheDotOnTheGameTab()
    {
        GameEntry blank = Game("iRacing") with { Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = string.Empty } };
        GameEditorViewModel editor = Editor(blank, isNew: true);
        editor.HasGameTabProblem.ShouldBeTrue();
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.SetExecutable(@"D:\Games\AMS2\AMS2AVX.exe");

        editor.HasGameTabProblem.ShouldBeFalse();
        changed.ShouldContain(nameof(GameEditorViewModel.HasGameTabProblem));
    }

    [Fact]
    public void Name_MissingTooLongOrTaken_IsAProblem()
    {
        GameEntry other = Game("Assetto Corsa");
        GameEntry game = Game("iRacing");
        GameEditorViewModel editor = Editor(game, games: [game, other]);

        editor.Name = "   ";
        editor.NameProblem.ShouldBe(Loc.Instance["Problem_NameMissing"]);

        editor.Name = new string('x', GameEntry.MaxNameLength + 1);
        editor.NameProblem.ShouldBe(Loc.Instance["Problem_NameTooLong"]);

        editor.Name = "assetto corsa";
        editor.NameProblem.ShouldBe(Loc.Instance["Problem_NameTaken"]);
        editor.ProblemCount.ShouldBe(1);

        editor.Name = "iRacing";
        editor.HasNameProblem.ShouldBeFalse();
        editor.ProblemCount.ShouldBe(0);
    }

    [Fact]
    public void NoLaunchTargetAndAnAppWithoutPath_AreCountedSeparately()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.LaunchTarget = " ";
        editor.AppList.Add(string.Empty);

        editor.HasLaunchProblem.ShouldBeTrue();
        editor.HasGameTabProblem.ShouldBeTrue();
        editor.HasAppsProblem.ShouldBeTrue();
        editor.ProblemCount.ShouldBe(2);

        editor.AppList.Items[0].Path = @"C:\Tools\CrewChief.exe";
        editor.HasAppsProblem.ShouldBeFalse();
        editor.ProblemCount.ShouldBe(1);
    }

    [Fact]
    public void HotkeyOfAnotherGameOrAProfile_IsAProblem_TheOwnOneIsNot()
    {
        Profile rig = Rig() with { Hotkey = CtrlAltR };
        GameEntry other = Game("Assetto Corsa") with { Hotkey = CtrlAltD };
        GameEntry game = Game("iRacing");
        GameEditorViewModel editor = Editor(game, profiles: [rig], games: [game, other]);

        editor.Hotkey = CtrlAltR;
        editor.HotkeyProblem.ShouldBe(Loc.Instance["Problem_HotkeyTaken"]);

        editor.Hotkey = CtrlAltD;
        editor.HasHotkeyProblem.ShouldBeTrue();

        editor.Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x70 };
        editor.HasHotkeyProblem.ShouldBeFalse();
    }

    [Fact]
    public void RecordHotkey_WithoutModifier_OnlyShowsTheHint()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.RecordHotkey(HotkeyModifiers.None, 0x52);

        editor.Hotkey.ShouldBeNull();
        editor.HotkeyHint.ShouldBe(Loc.Instance["Editor_HotkeyNeedsModifier"]);
    }

    [Fact]
    public void RecordHotkey_HeldByAnotherApplication_IsRefused()
    {
        _registrar.TakenElsewhere.Add(CtrlAltR);
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);

        editor.Hotkey.ShouldBeNull();
        editor.HotkeyHint.ShouldBe(Loc.Instance["Problem_HotkeyInUse"]);
    }

    [Fact]
    public async Task RecordHotkey_HeldByAProfile_NamesTheProfile()
    {
        await _host.Catalog.SaveAsync(Rig() with { Hotkey = CtrlAltR }, Ct);
        GameEditorViewModel editor = Editor(Game("iRacing"));
        editor.BeginHotkeyRecording();

        editor.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);
        editor.EndHotkeyRecording();

        editor.Hotkey.ShouldBeNull();
        editor.HotkeyHint.ShouldContain("Rig");
    }

    [Fact]
    public void RecordHotkey_FreeCombination_IsTaken_AndClearRemovesIt()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.RecordHotkey(CtrlAltD.Modifiers, CtrlAltD.VirtualKey);

        editor.Hotkey.ShouldBe(CtrlAltD);
        editor.HasHotkey.ShouldBeTrue();
        editor.HotkeyHint.ShouldBe(Loc.Instance["Game_HotkeyHint"]);
        _registrar.Held.ShouldBeEmpty("the probe must not keep the combination");

        editor.ClearHotkeyCommand.Execute(null);
        editor.Hotkey.ShouldBeNull();
        editor.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void SetLaunch_KnownSim_FillsNameProcessesAndSessionEndFromTheTemplate()
    {
        GameEntry blank = Game(Loc.Instance["Games_NewName"]) with { Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = string.Empty } };
        GameEditorViewModel editor = Editor(blank, isNew: true);
        var launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", InstallFolder = @"D:\Steam\iRacing" };
        GameEntry expected = SimTemplates.Apply(new GameEntry { Id = blank.Id, Name = "iRacing", Launch = launch });

        editor.SetLaunch(new InstalledGame("iRacing", launch));

        editor.Name.ShouldBe("iRacing");
        editor.LaunchKindText.ShouldBe("Steam");
        editor.LaunchText.ShouldBe(@"D:\Steam\iRacing");
        editor.HasLaunch.ShouldBeTrue();
        GameEntry built = editor.ToGame();
        built.Launch.ProcessName.ShouldBe(expected.Launch.ProcessName);
        built.LauncherProcessName.ShouldBe(expected.LauncherProcessName);
        built.EndsWith.ShouldBe(expected.EndsWith);
    }

    [Fact]
    public void SetLaunch_KeepsANameTheUserGave()
    {
        GameEditorViewModel editor = Editor(Game("My sim"));

        editor.SetLaunch(new InstalledGame("iRacing", new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" }));

        editor.Name.ShouldBe("My sim");
    }

    [Fact]
    public void SetExecutable_ForgetsTheLearnedProcess_AndNamesAnUnnamedGameAfterTheFile()
    {
        GameEntry game = Game(string.Empty) with { Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", ProcessName = "iRacingSim64DX11" } };
        GameEditorViewModel editor = Editor(game);

        editor.SetExecutable(@"D:\Games\AMS2\AMS2AVX.exe");

        editor.Name.ShouldBe("AMS2AVX");
        editor.ProcessName.ShouldBeEmpty();
        editor.LaunchKindText.ShouldBe("EXE");
        editor.LaunchText.ShouldBe(@"D:\Games\AMS2\AMS2AVX.exe");
        editor.ToGame().Launch.ShouldBe(new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"D:\Games\AMS2\AMS2AVX.exe" });
    }

    [Fact]
    public void ProcessNotRecognised_TurnsTheHintIntoAnError_UntilTheFieldChanges()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.ProcessNotRecognised = true;
        editor.ProcessHintIsError.ShouldBeTrue();
        editor.ProcessHint.ShouldBe(Loc.Instance["Game_ProcessNotRecognised"]);

        editor.ProcessName = "iRacingSim64DX11";
        editor.ProcessNotRecognised.ShouldBeFalse();
        editor.ProcessHint.ShouldBe(Loc.Instance["Game_ProcessNameHint"]);
    }

    [Fact]
    public void Apps_MoveAndRemove_KeepTheOrderThatIsSaved()
    {
        GameEntry game = Game("iRacing") with
        {
            Apps = [new AppAction { Path = @"C:\a.exe" }, new AppAction { Path = @"C:\b.exe" }, new AppAction { Path = @"C:\c.exe" }],
        };
        GameEditorViewModel editor = Editor(game);

        editor.AppList.MoveUpCommand.Execute(editor.AppList.Items[0]);
        editor.IsDirty.ShouldBeFalse("the first app cannot move up");

        editor.AppList.MoveDownCommand.Execute(editor.AppList.Items[0]);
        editor.AppList.RemoveCommand.Execute(editor.AppList.Items[2]);

        editor.ToGame().Apps.Select(a => a.Path).ShouldBe([@"C:\b.exe", @"C:\a.exe"]);
        editor.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public void ExitChoice_PreviousProfile_KeepsTheStopAppsSwitch()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.SelectedExit = editor.ExitChoices.First(c => c.Key == nameof(GameExitKind.PreviousProfile));
        editor.StopApps = false;

        editor.ToGame().Exit.ShouldBe(new GameExitAction { Kind = GameExitKind.PreviousProfile, StopApps = false });
    }

    [Fact]
    public void UsbDevice_IsSavedWithWindowsNameForIt()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.AppList.WaitDevice.Selected = editor.AppList.WaitDevice.Choices.First(c => c.Key == Wheelbase);

        GameEntry built = editor.ToGame();
        built.AppsWaitForUsbDeviceId.ShouldBe(Wheelbase);
        built.AppsWaitForUsbDeviceName.ShouldBe("Fanatec Wheelbase");
    }

    /// <summary>The profile is gone: the editor must not pretend the game still switches to it.</summary>
    [Fact]
    public void GameWhoseProfileWasDeleted_OpensWithNoProfile_AndAsksToBeSaved()
    {
        GameEntry game = Game("iRacing") with { ProfileId = Guid.NewGuid() };

        GameEditorViewModel editor = Editor(game, profiles: [Rig()]);

        editor.SelectedProfile.ShouldNotBeNull().Key.ShouldBeNull();
        editor.IsDirty.ShouldBeTrue();
        editor.ToGame().ProfileId.ShouldBeNull();
    }

    [Fact]
    public void EmptyWindowLayout_IsSavedAsNone()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"));

        editor.WindowLayout = new WindowLayout { CapturedAt = DateTimeOffset.UtcNow, Windows = [] };

        editor.HasWindows.ShouldBeFalse();
        editor.SavedWindows.ShouldBeEmpty();
        editor.WindowsText.ShouldBe(Loc.Instance["Game_WindowsNone"]);
        editor.ToGame().WindowLayout.ShouldBeNull();
        editor.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public void CommandText_EscapesTheName()
    {
        GameEditorViewModel editor = Editor(Game("Assetto Corsa"));

        editor.CommandText.ShouldBe("rigshift://play/Assetto%20Corsa");
    }

    [Fact]
    public async Task Save_WritesTheGame_AndTheEditorIsCleanAndNoLongerNew()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"), isNew: true);
        editor.Name = "  iRacing  ";

        (await editor.SaveAsync()).ShouldBeTrue();

        _store.Games.ShouldHaveSingleItem().Name.ShouldBe("iRacing");
        editor.IsNew.ShouldBeFalse();
        editor.IsDirty.ShouldBeFalse();
        editor.HasError.ShouldBeFalse();
    }

    [Fact]
    public async Task Save_WithAProblem_WritesNothing()
    {
        GameEditorViewModel editor = Editor(Game("iRacing"), isNew: true);
        editor.Name = string.Empty;

        (await editor.SaveAsync()).ShouldBeFalse();

        _store.Games.ShouldBeEmpty();
        editor.IsNew.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_DiskSaysNo_ShowsTheError_AndTheNextGoodSaveClearsIt()
    {
        IGameStore store = Substitute.For<IGameStore>();
        store.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new GameLoadResult([], null));
        store.SaveAsync(Arg.Any<GameEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full", unchecked((int)0x80070070)));
        var catalog = new GameCatalog(store, _host.Catalog, Logger.None);
        GameEditorViewModel editor = Editor(Game("iRacing"), isNew: true, catalog: catalog);

        (await editor.SaveAsync()).ShouldBeFalse();

        editor.ErrorMessage.ShouldBe(Loc.Instance["Error_DiskFull"]);
        editor.IsNew.ShouldBeTrue();
        editor.IsDirty.ShouldBeTrue();

        store.SaveAsync(Arg.Any<GameEntry>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        (await editor.SaveAsync()).ShouldBeTrue();
        editor.HasError.ShouldBeFalse();
    }

    public void Dispose()
    {
        foreach (GameEditorViewModel editor in _editors)
        {
            editor.Dispose();
        }

        _hotkeys.Dispose();
        _sessions.Dispose();
        _host.Dispose();
    }

    private static GameEntry Game(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
    };

    private GameEditorViewModel Editor(
        GameEntry game,
        bool isNew = false,
        IReadOnlyList<Profile>? profiles = null,
        IReadOnlyList<GameEntry>? games = null,
        GameCatalog? catalog = null)
    {
        var context = new GameEditorContext(
            profiles ?? [],
            games ?? [game],
            new AppsWaitDeviceChoice(game.AppsWaitForUsbDeviceId, game.AppsWaitForUsbDeviceName, [new UsbDevice(Wheelbase, "Fanatec Wheelbase")], [], null));
        var editor = new GameEditorViewModel(
            game, isNew, context, new GameEditorServices(catalog ?? _catalog, _hotkeys, new FakeAppPicker(), Logger.None));
        _editors.Add(editor);
        return editor;
    }
}
