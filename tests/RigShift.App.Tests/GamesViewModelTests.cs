using NSubstitute;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Cli;
using RigShift.Core.Games;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>
/// The games page without its window: list and selection, what happens to unsaved changes, delete, and how a session
/// shows. Everything runs on one dispatcher thread, as it does in the app.
/// </summary>
public sealed class GamesViewModelTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly DispatcherThread _ui = new();
    private readonly AppTestHost _host;
    private readonly InMemoryGameStore _store = new();
    private readonly GameCatalog _catalog;
    private readonly IGameStarter _starter = Substitute.For<IGameStarter>();
    private readonly IGameProcesses _processes = Substitute.For<IGameProcesses>();
    private readonly GameSessionService _sessions;
    private readonly HotkeyService _hotkeys;
    private readonly Dialogs _dialogs;

    public GamesViewModelTests()
    {
        _host = _ui.Invoke(() => new AppTestHost(new FakeDisplayConfigurator(DeskActive())));
        _catalog = _ui.Invoke(() => new GameCatalog(_store, _host.Catalog, Logger.None));
        _processes.List().Returns([]);
        _sessions = _ui.Invoke(() => new GameSessionService(_catalog, _host.Catalog, _processes, Runner, TimeProvider.System, Logger.None));
        _hotkeys = _ui.Invoke(() => new HotkeyService(
            _host.Catalog, _catalog, _sessions, _host.Coordinator, _host.Settings, Logger.None, new FakeHotkeyRegistrar()));
        _dialogs = new Dialogs(new GameDialogs(
            _catalog, _host.Catalog, Substitute.For<IGameLibrary>(), Substitute.For<IWindowLayout>(), _host.Usb, _host.Settings, _hotkeys, Logger.None));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task NoGames_TheListIsEmpty_AndThereIsNothingToPlay() => _ui.RunAsync(async () =>
    {
        GamesViewModel page = await PageAsync();

        page.IsEmpty.ShouldBeTrue();
        page.HasSelection.ShouldBeFalse();
        page.CanPlay.ShouldBeFalse();
        page.HeadStatusText.ShouldBeEmpty();
    });

    [Fact]
    public Task SavedGames_TheFirstIsSelected_WithItsEditorAndReadyToPlay() => _ui.RunAsync(async () =>
    {
        GameEntry iracing = await SavedAsync("iRacing", process: "iRacingSim64DX11");
        await SavedAsync("Assetto Corsa");

        GamesViewModel page = await PageAsync();

        page.Items.Select(i => i.Name).ShouldBe(["iRacing", "Assetto Corsa"]);
        page.SelectedItem.ShouldNotBeNull().Game.Id.ShouldBe(iracing.Id);
        page.Editor.ShouldNotBeNull().Id.ShouldBe(iracing.Id);
        page.CanPlay.ShouldBeTrue();
        page.PlayLabel.ShouldBe(Loc.Instance["Game_Play"]);
        page.HeadStatusKind.ShouldBe(StatusKind.Ok);
        page.HeadStatusText.ShouldBe(Loc.Instance["List_Ready"]);
    });

    [Fact]
    public Task GameWhoseProcessIsNotKnownYet_SaysSoInTheList() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");

        GamesViewModel page = await PageAsync();

        GameItem item = page.Items.ShouldHaveSingleItem();
        item.ListKind.ShouldBe(StatusKind.Warn);
        item.ListStatus.ShouldBe(Loc.Instance["Game_ProcessUnknownStatus"]);
    });

    [Fact]
    public Task UnsavedChange_LocksPlay_AndTheHeadSaysUnsaved() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing", process: "iRacingSim64DX11");
        GamesViewModel page = await PageAsync();

        page.Editor.ShouldNotBeNull().Name = "iRacing 2";

        page.CanPlay.ShouldBeFalse();
        page.HeadStatusKind.ShouldBe(StatusKind.Neutral);
        page.HeadStatusText.ShouldBe(Loc.Instance["SaveBar_Unsaved"]);
    });

    [Fact]
    public Task Save_WritesTheGame_KeepsItSelected_AndShowsTheStatus() => _ui.RunAsync(async () =>
    {
        GameEntry game = await SavedAsync("iRacing");
        await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "iRacing 2";

        await page.SaveCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsDirty: false, Name: "iRacing 2" }, "the saved game was not loaded again");

        _store.Games.Select(g => g.Name).ShouldBe(["iRacing 2", "Assetto Corsa"]);
        page.SelectedItem.ShouldNotBeNull().Game.Id.ShouldBe(game.Id);
        page.StatusMessage.ShouldBe(Loc.Format("Status_Saved", "iRacing 2"));
    });

    [Fact]
    public Task Discard_BringsTheSavedGameBack() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "something else";

        page.DiscardCommand.Execute(null);
        await UntilAsync(() => page.Editor is { IsDirty: false }, "the editor was not reloaded");

        page.Editor.ShouldNotBeNull().Name.ShouldBe("iRacing");
    });

    [Fact]
    public Task SelectingAnotherGame_WithUnsavedChanges_Stay_KeepsTheSelectionAndTheChanges() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        GameEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "iRacing 2";
        _dialogs.Unsaved = UnsavedChoice.Cancel;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => _dialogs.UnsavedAsked.Count == 1 && page.SelectedItem == page.Items[0], "the selection did not go back");

        _dialogs.UnsavedAsked.ShouldHaveSingleItem().ShouldBe(("iRacing 2", "Assetto Corsa"));
        page.Editor.ShouldBeSameAs(editor);
        editor.IsDirty.ShouldBeTrue();
    });

    [Fact]
    public Task SelectingAnotherGame_WithUnsavedChanges_Discard_OpensTheOtherGame_AndWritesNothing() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GameEntry other = await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "iRacing 2";
        _dialogs.Unsaved = UnsavedChoice.Discard;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => page.Editor?.Id == other.Id, "the other game was not opened");

        _store.Games.Select(g => g.Name).ShouldBe(["iRacing", "Assetto Corsa"]);
    });

    /// <summary>The dialog names the game the user is going to – and then stayed on the saved one, because saving rebuilds the list.</summary>
    [Fact]
    public Task SelectingAnotherGame_WithUnsavedChanges_Save_WritesThem_AndOpensTheOtherGame() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GameEntry other = await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "iRacing 2";
        _dialogs.Unsaved = UnsavedChoice.Save;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => page.Editor?.Id == other.Id, "the other game was not opened");

        _store.Games.Select(g => g.Name).ShouldBe(["iRacing 2", "Assetto Corsa"]);
        page.SelectedItem.ShouldNotBeNull().Game.Id.ShouldBe(other.Id);
    });

    /// <summary>Discarding a new game used to land on the first game of the list, not on the one that was clicked.</summary>
    [Fact]
    public Task SelectingAnotherGame_FromAnUnsavedNewOne_Discard_OpensTheGameThatWasPicked() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GameEntry other = await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        _dialogs.Executable = @"D:\Games\AMS2\AMS2AVX.exe";
        await page.AddProgramCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new game's editor did not open");
        _dialogs.Unsaved = UnsavedChoice.Discard;

        page.SelectedItem = page.Items.Single(i => i.Game.Id == other.Id);
        await UntilAsync(() => page.Editor?.Id == other.Id, "the picked game was not opened");

        page.Items.Select(i => i.Name).ShouldBe(["iRacing", "Assetto Corsa"]);
        page.SelectedItem.ShouldNotBeNull().Game.Id.ShouldBe(other.Id);
    });

    [Fact]
    public Task ConfirmLeave_SaveWithAProblem_Stays() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = string.Empty;
        _dialogs.Unsaved = UnsavedChoice.Save;

        (await page.ConfirmLeaveAsync()).ShouldBeFalse();

        _dialogs.UnsavedAsked.ShouldHaveSingleItem().Name.ShouldBe(Loc.Instance["Games_NewName"]);
        _store.Games.ShouldHaveSingleItem().Name.ShouldBe("iRacing");
    });

    [Fact]
    public Task ConfirmLeave_WithoutChanges_AsksNothing() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();

        (await page.ConfirmLeaveAsync()).ShouldBeTrue();

        _dialogs.UnsavedAsked.ShouldBeEmpty();
    });

    [Fact]
    public Task AddProgram_PutsAnUnsavedGameOnTop_NamedAfterTheFile_OnTheProfileTab() => _ui.RunAsync(async () =>
    {
        await SavedAsync("AMS2AVX");
        GamesViewModel page = await PageAsync();
        bool focusAsked = false;
        page.FocusNameRequested += (_, _) => focusAsked = true;
        _dialogs.Executable = @"D:\Games\AMS2\AMS2AVX.exe";

        await page.AddProgramCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new game's editor did not open");

        page.Items.Count.ShouldBe(2);
        GameItem item = page.Items[0];
        item.IsNew.ShouldBeTrue();
        item.Name.ShouldNotBe("AMS2AVX", "the name is taken");
        item.ListStatus.ShouldBe(Loc.Instance["List_Unsaved"]);
        page.SelectedItem.ShouldBe(item);
        page.SelectedTabIndex.ShouldBe(1);
        page.CanPlay.ShouldBeFalse();
        focusAsked.ShouldBeTrue();
        _store.Games.ShouldHaveSingleItem();
    });

    [Fact]
    public Task AddProgram_Cancelled_ChangesNothing() => _ui.RunAsync(async () =>
    {
        GamesViewModel page = await PageAsync();

        await page.AddProgramCommand.ExecuteAsync(null);

        page.IsEmpty.ShouldBeTrue();
    });

    [Fact]
    public Task NewGame_Saved_BecomesAnOrdinaryEntry() => _ui.RunAsync(async () =>
    {
        GamesViewModel page = await PageAsync();
        _dialogs.Executable = @"D:\Games\AMS2\AMS2AVX.exe";
        await page.AddProgramCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new game's editor did not open");

        await page.SaveCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Items.All(i => !i.IsNew) && page.Editor is { IsNew: false, IsDirty: false }, "the unsaved entry stayed in the list");

        GameItem item = page.Items.ShouldHaveSingleItem();
        item.IsNew.ShouldBeFalse();
        page.SelectedItem.ShouldBe(item);
        _store.Games.ShouldHaveSingleItem().Name.ShouldBe("AMS2AVX");
    });

    [Fact]
    public Task NewGame_Discarded_DisappearsFromTheList() => _ui.RunAsync(async () =>
    {
        GameEntry saved = await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        _dialogs.Executable = @"D:\Games\AMS2\AMS2AVX.exe";
        await page.AddProgramCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new game's editor did not open");

        page.DiscardCommand.Execute(null);
        await UntilAsync(() => page.Editor?.Id == saved.Id, "the saved game was not opened");

        page.Items.ShouldHaveSingleItem().Game.Id.ShouldBe(saved.Id);
    });

    [Fact]
    public Task AddInstalled_SelectsTheFirstNewGame_AndMentionsTheSkippedOnes() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        var added = new GameEntry { Id = Guid.NewGuid(), Name = "rFactor 2", Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "365960" } };
        _dialogs.Installed = async () =>
        {
            await _catalog.SaveAsync(added, Ct);

            // The real store writes a file, so the list has followed the catalog by the time the picker's result is in.
            await Task.Yield();
            return new AddedGames([added], Skipped: 1);
        };

        await page.AddInstalledCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor?.Id == added.Id, "the added game was not opened");

        page.SelectedItem.ShouldNotBeNull().Game.Id.ShouldBe(added.Id);
        page.SelectedTabIndex.ShouldBe(1);
        page.StatusMessage.ShouldBe(Loc.Format("Games_AddedOne", "rFactor 2"));
        page.DetailMessage.ShouldBe(Loc.Format("Games_AddedSkipped", 1));
        page.DetailKind.ShouldBe(InfoKind.Info);
    });

    [Fact]
    public Task Delete_Confirmed_RemovesTheGame_AndSelectsTheNextOne() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GameEntry next = await SavedAsync("Assetto Corsa");
        GamesViewModel page = await PageAsync();
        _dialogs.Delete = true;

        await page.DeleteCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor?.Id == next.Id, "the next game was not opened");

        _store.Games.ShouldHaveSingleItem().Id.ShouldBe(next.Id);
        page.Items.ShouldHaveSingleItem();
        page.StatusMessage.ShouldBe(Loc.Format("Status_Deleted", "iRacing"));
    });

    [Fact]
    public Task Delete_NotConfirmed_KeepsTheGame() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();

        await page.DeleteCommand.ExecuteAsync(null);

        _dialogs.DeleteAsked.ShouldBe(["iRacing"]);
        _store.Games.ShouldHaveSingleItem();
        page.Editor.ShouldNotBeNull();
    });

    [Fact]
    public Task Play_MarksTheGameAsRunning_LocksPlay_AndTheEndShowsInTheList() => _ui.RunAsync(async () =>
    {
        GameEntry game = await SavedAsync("iRacing", process: "iRacingSim64DX11", executable: true);
        using var held = new HeldGame();
        _starter.Start(Arg.Any<GameLaunch>()).Returns(held);
        GamesViewModel page = await PageAsync();

        page.PlayCommand.Execute(null);
        await UntilAsync(() => page.IsSelectedRunning, "the session did not start");

        page.CanPlay.ShouldBeFalse();
        page.PlayLabel.ShouldBe(Loc.Instance["Game_Running"]);
        page.HeadStatusKind.ShouldBe(StatusKind.Accent);
        page.Items[0].IsRunning.ShouldBeTrue();

        held.Dispose();
        await UntilAsync(() => !_sessions.IsRunning(game.Id) && page.CanPlay, "the session did not end");

        page.IsSelectedRunning.ShouldBeFalse();
        page.Items[0].ListKind.ShouldBe(StatusKind.Ok);
        page.DetailKind.ShouldBe(InfoKind.Info);
    });

    [Fact]
    public Task Play_TheStartFails_ShowsTheErrorInTheDetailAndTheList() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing", process: "iRacingSim64DX11", executable: true);
        _starter.Start(Arg.Any<GameLaunch>()).Returns(_ => throw new System.ComponentModel.Win32Exception(2));
        GamesViewModel page = await PageAsync();

        page.PlayCommand.Execute(null);
        await UntilAsync(() => page.HasDetailMessage, "the failure was not shown");

        page.DetailKind.ShouldBe(InfoKind.Error);
        page.DetailOffersGameTab.ShouldBeFalse();
        page.Items[0].ListKind.ShouldBe(StatusKind.Error);

        page.CloseDetailMessageCommand.Execute(null);
        page.HasDetailMessage.ShouldBeFalse();
    });

    /// <summary>The first start learned the process name while the game was being edited (v4 finding A-01).</summary>
    [Fact]
    public Task DirtyEditor_ProcessNameLearned_KeepsTheChanges_AndOffersAReload() => _ui.RunAsync(async () =>
    {
        GameEntry game = await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        GameEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "iRacing 2";
        int created = _dialogs.EditorsCreated;

        await _catalog.RememberProcessNameAsync(game.Id, "iRacingSim64DX11", Ct);

        _dialogs.EditorsCreated.ShouldBe(created);
        page.Editor.ShouldBeSameAs(editor);
        editor.Name.ShouldBe("iRacing 2");
        page.IsStale.ShouldBeTrue();
    });

    [Fact]
    public Task CleanEditor_ProcessNameLearned_ShowsIt() => _ui.RunAsync(async () =>
    {
        GameEntry game = await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        GameEditorViewModel editor = page.Editor.ShouldNotBeNull();

        await _catalog.RememberProcessNameAsync(game.Id, "iRacingSim64DX11", Ct);
        await UntilAsync(() => page.Editor is { } fresh && fresh != editor, "the editor was not reloaded");

        page.Editor.ShouldNotBeNull().ProcessName.ShouldBe("iRacingSim64DX11");
        page.IsStale.ShouldBeFalse();
    });

    /// <summary>A renamed profile rebuilds every game card; a game being edited keeps its changes.</summary>
    [Fact]
    public Task DirtyEditor_SurvivesAProfileBeingSaved() => _ui.RunAsync(async () =>
    {
        await SavedAsync("iRacing");
        GamesViewModel page = await PageAsync();
        GameEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "iRacing 2";
        int created = _dialogs.EditorsCreated;

        await _host.Catalog.SaveAsync(Profile("Desk", DeskModes), Ct);

        _dialogs.EditorsCreated.ShouldBe(created);
        page.Editor.ShouldBeSameAs(editor);
        page.IsStale.ShouldBeFalse();
    });

    [Fact]
    public Task NewUnsavedGame_SurvivesAnotherGameBeingSaved() => _ui.RunAsync(async () =>
    {
        GamesViewModel page = await PageAsync();
        _dialogs.Executable = @"C:\Games\rFactor2.exe";
        await page.AddProgramCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new game's editor did not open");
        GameEditorViewModel editor = page.Editor.ShouldNotBeNull();

        await SavedAsync("iRacing");

        page.Items.Count.ShouldBe(2);
        page.Items[0].IsNew.ShouldBeTrue();
        page.Editor.ShouldBeSameAs(editor);
    });

    public void Dispose()
    {
        _ui.Invoke(() =>
        {
            _hotkeys.Dispose();
            _sessions.Dispose();
        });
        _ui.Dispose();
        _host.Dispose();
    }

    private GameSessionRunner Runner() => new(
        _starter, _processes, Substitute.For<IProfileSwitcher>(), _ => null,
        Substitute.For<IAppLauncher>(), _host.Usb, new SwitchOptions(), new AutoAdvanceTimeProvider(), Logger.None);

    private async Task<GameEntry> SavedAsync(string name, string? process = null, bool executable = false)
    {
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = name,
            Launch = executable
                ? new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\Games\" + name + ".exe", ProcessName = process }
                : new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", ProcessName = process },
        };
        await _catalog.SaveAsync(game, Ct);
        return game;
    }

    /// <summary>The page with its first editor loaded; that happens in the background after the constructor.</summary>
    private async Task<GamesViewModel> PageAsync()
    {
        var page = new GamesViewModel(_catalog, _sessions, _dialogs, _host.Paths, Logger.None);
        await UntilAsync(() => page.IsEmpty || page.Editor is not null, "the first editor did not open");
        return page;
    }

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(10, Ct);
        }
    }

    /// <summary>The real editor factory, and scripted answers where the app would open a window.</summary>
    private sealed class Dialogs(GameDialogs real) : IGamePageDialogs
    {
        public UnsavedChoice Unsaved { get; set; } = UnsavedChoice.Cancel;

        public bool Delete { get; set; }

        public string? Executable { get; set; }

        public Func<Task<AddedGames>> Installed { get; set; } = () => Task.FromResult(new AddedGames([], 0));

        public List<(string Name, string? Target)> UnsavedAsked { get; } = [];

        public List<string> DeleteAsked { get; } = [];

        /// <summary>How many editors the page asked for; counted when asked, before the editor is ready.</summary>
        public int EditorsCreated { get; private set; }

        public Task<GameEditorViewModel> CreateEditorAsync(GameEntry game, bool isNew)
        {
            EditorsCreated++;
            return real.CreateEditorAsync(game, isNew);
        }

        public Task<AddedGames> AddInstalledAsync() => Installed();

        public string? PickExecutable() => Executable;

        public Task<bool> ConfirmDeleteAsync(string name)
        {
            DeleteAsked.Add(name);
            return Task.FromResult(Delete);
        }

        public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName)
        {
            UnsavedAsked.Add((name, targetName));
            return Task.FromResult(Unsaved);
        }
    }

    private sealed class HeldGame : IRunningGame
    {
        private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Id => 4711;

        public Task WaitForExitAsync(CancellationToken cancellationToken) => _exit.Task.WaitAsync(cancellationToken);

        public void Dispose() => _exit.TrySetResult();
    }
}
