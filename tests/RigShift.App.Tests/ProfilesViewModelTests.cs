using NSubstitute;
using RigShift.App.Controls;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
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

/// <summary>
/// The profiles page without its window: list and status lines, what happens to unsaved changes, duplicate, default,
/// delete, test and switch. Everything runs on one dispatcher thread, as it does in the app.
/// </summary>
public sealed class ProfilesViewModelTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly DispatcherThread _ui = new();
    private readonly FakeDisplayConfigurator _display;
    private readonly AppTestHost _host;
    private readonly InMemoryGameStore _gameStore = new();
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private readonly HotkeyService _hotkeys;
    private readonly DisplayChangeWatcher _watcher;
    private readonly Dialogs _dialogs;

    public ProfilesViewModelTests()
    {
        _display = new FakeDisplayConfigurator(DeskActive(ultrawideAvailable: false));
        _host = _ui.Invoke(() => new AppTestHost(_display));
        _host.Usb.ConnectedDevices().Returns([]);
        _games = _ui.Invoke(() => new GameCatalog(_gameStore, _host.Catalog, Logger.None));
        _sessions = _ui.Invoke(() => new GameSessionService(
            _games, _host.Catalog, Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"), TimeProvider.System, Logger.None));
        _hotkeys = _ui.Invoke(() => new HotkeyService(
            _host.Catalog, _games, _sessions, _host.Coordinator, _host.Settings, Logger.None, new FakeHotkeyRegistrar()));
        _watcher = _ui.Invoke(() => new DisplayChangeWatcher());

        IAudioController audio = Substitute.For<IAudioController>();
        audio.ListAsync(Arg.Any<AudioDirection>(), Arg.Any<CancellationToken>()).Returns([]);
        ISurroundController surround = Substitute.For<ISurroundController>();
        surround.QueryAsync(Arg.Any<CancellationToken>()).Returns(SurroundState.Unavailable(SurroundAvailability.Unknown));
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        IServiceProvider services = Substitute.For<IServiceProvider>();
        services.GetService(typeof(IUsbPowerCheck)).Returns(powerCheck);
        _dialogs = new Dialogs(new ProfileDialogs(
            _host.Catalog, _display, audio, _host.Settings, _hotkeys, _host.Usb, Substitute.For<IDesktopIcons>(), surround, services, Logger.None));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public Task NoProfiles_TheListIsEmpty_AndTheCurrentArrangementIsOffered() => _ui.RunAsync(async () =>
    {
        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.CurrentTopology.Count == 3, "the current arrangement was not read");

        page.IsEmpty.ShouldBeTrue();
        page.HasSelection.ShouldBeFalse();
        page.CanSwitch.ShouldBeFalse();
    });

    [Fact]
    public Task StatusLines_ActiveDefaultReadyAndBlocked() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        await SavedAsync(Rig());
        await _host.Catalog.ToggleDefaultAsync(desk, Ct);

        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.Items.All(i => i.ListStatus != Loc.Instance["List_Checking"]), "the profiles were not planned");

        page.Items.Select(i => (i.Name, i.ListKind, i.ListStatus)).ShouldBe(
        [
            ("Desk", StatusKind.Ok, Loc.Instance["Profile_Active"] + " · " + Loc.Instance["Profile_Default"]),
            ("Side", StatusKind.Ok, Loc.Instance["List_Ready"]),
            ("Rig", StatusKind.Error, Loc.Instance["List_BlockedShort"]),
        ]);
        page.IsSelectedActive.ShouldBeTrue();
        page.IsSelectedDefault.ShouldBeTrue();
        page.SwitchIsPrimary.ShouldBeFalse();
        page.SwitchLabel.ShouldBe(Loc.Instance["Profile_Reapply"]);
    });

    [Fact]
    public Task BlockedProfile_CannotBeSwitchedTo_AndTheHeadNamesWhatIsMissing() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Rig());

        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.HasBlockedMessage, "the missing display was not reported");

        page.CanSwitch.ShouldBeFalse();
        page.BlockedMessage.ShouldNotBeNull().ShouldContain("Ultrawide 49");
        page.HeadStatusKind.ShouldBe(StatusKind.Error);
        page.HeadStatusText.ShouldBe(Loc.Instance["Head_MissingOne"]);
        page.Editor.ShouldNotBeNull().TopologyDisplays.Count(d => d.State == TopologyDisplayState.Missing).ShouldBe(1);
    });

    [Fact]
    public Task UnsavedChange_LocksSwitch_AndTheHeadSaysUnsaved() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.CanSwitch, "the profile was not ready");

        page.Editor.ShouldNotBeNull().KeepAwake = true;

        page.CanSwitch.ShouldBeFalse();
        page.SwitchIsPrimary.ShouldBeFalse();
        page.HeadStatusText.ShouldBe(Loc.Instance["SaveBar_Unsaved"]);
    });

    [Fact]
    public Task Save_WritesTheProfile_KeepsItSelected_AndShowsTheStatus() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "Desk 2";

        await page.SaveCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsDirty: false, Name: "Desk 2" } && page.Items[0].Name == "Desk 2", "the saved profile was not loaded again");

        _host.Store.Profiles.Select(p => p.Name).ShouldBe(["Desk 2", "Side"], ignoreOrder: true);
        page.SelectedItem.ShouldNotBeNull().Profile.Id.ShouldBe(desk.Id);
        page.StatusMessage.ShouldBe(Loc.Format("Status_Saved", "Desk 2"));
    });

    [Fact]
    public Task SelectingAnotherProfile_WithUnsavedChanges_Stay_KeepsTheSelectionAndTheChanges() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";
        _dialogs.Unsaved = UnsavedChoice.Cancel;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => _dialogs.UnsavedAsked.Count == 1 && page.SelectedItem == page.Items[0], "the selection did not go back");

        _dialogs.UnsavedAsked.ShouldHaveSingleItem().ShouldBe(("Desk 2", "Side"));
        page.Editor.ShouldBeSameAs(editor);
        editor.IsDirty.ShouldBeTrue();
    });

    /// <summary>The dialog names the profile the user is going to – and then stayed on the saved one, because saving rebuilds the list.</summary>
    [Fact]
    public Task SelectingAnotherProfile_WithUnsavedChanges_Save_WritesThem_AndOpensTheOtherProfile() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        Profile side = await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "Desk 2";
        _dialogs.Unsaved = UnsavedChoice.Save;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => page.Editor?.Id == side.Id, "the other profile was not opened");

        _host.Store.Profiles.Select(p => p.Name).ShouldBe(["Desk 2", "Side"], ignoreOrder: true);
        page.SelectedItem.ShouldNotBeNull().Profile.Id.ShouldBe(side.Id);
    });

    [Fact]
    public Task SelectingAnotherProfile_WithUnsavedChanges_Discard_OpensTheOtherProfile_AndWritesNothing() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        Profile side = await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        page.Editor.ShouldNotBeNull().Name = "Desk 2";
        _dialogs.Unsaved = UnsavedChoice.Discard;

        page.SelectedItem = page.Items[1];
        await UntilAsync(() => page.Editor?.Id == side.Id, "the other profile was not opened");

        _host.Store.Profiles.Select(p => p.Name).ShouldBe(["Desk", "Side"], ignoreOrder: true);
    });

    [Fact]
    public Task NewFromCurrent_PutsAnUnsavedProfileOnTop_WithTheDisplaysThatAreOn() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        bool focusAsked = false;
        page.FocusNameRequested += (_, _) => focusAsked = true;

        await page.NewFromCurrentCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new profile's editor did not open");

        page.Items.Count.ShouldBe(2);
        page.Items[0].IsNew.ShouldBeTrue();
        page.Items[0].ListStatus.ShouldBe(Loc.Instance["List_Unsaved"]);
        page.Editor.ShouldNotBeNull().Displays.Count.ShouldBe(3);
        page.CanSwitch.ShouldBeFalse();
        page.TestCommand.CanExecute(null).ShouldBeFalse();
        focusAsked.ShouldBeTrue();
        _host.Store.Profiles.ShouldHaveSingleItem();
    });

    /// <summary>The list is rebuilt while the save still runs; the unsaved entry used to stay next to the saved one, with the save bar up.</summary>
    [Fact]
    public Task NewProfile_Saved_BecomesAnOrdinaryEntry() => _ui.RunAsync(async () =>
    {
        ProfilesViewModel page = await PageAsync();
        await page.NewFromCurrentCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new profile's editor did not open");

        await page.SaveCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Items.All(i => !i.IsNew) && page.Editor is { IsNew: false, IsDirty: false }, "the unsaved entry stayed in the list");

        page.Items.ShouldHaveSingleItem().Profile.Id.ShouldBe(_host.Store.Profiles.ShouldHaveSingleItem().Id);
    });

    /// <summary>Discarding a new profile used to land on the first profile of the list, not on the one that was clicked.</summary>
    [Fact]
    public Task SelectingAnotherProfile_FromAnUnsavedNewOne_Discard_OpensTheProfileThatWasPicked() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        Profile side = await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        await page.NewFromCurrentCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new profile's editor did not open");
        _dialogs.Unsaved = UnsavedChoice.Discard;

        page.SelectedItem = page.Items.Single(i => i.Profile.Id == side.Id);
        await UntilAsync(() => page.Editor?.Id == side.Id, "the picked profile was not opened");

        page.Items.Select(i => i.Name).ShouldBe(["Desk", "Side"]);
        page.SelectedItem.ShouldNotBeNull().Profile.Id.ShouldBe(side.Id);
    });

    [Fact]
    public Task Duplicate_SavesACopyUnderAFreeName_AndSelectsIt() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();

        await page.DuplicateCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { } e && e.Id != desk.Id, "the copy was not opened");

        _host.Store.Profiles.Count.ShouldBe(2);
        Profile copy = _host.Store.Profiles.Single(p => p.Id != desk.Id);
        copy.Name.ShouldBe(Loc.Format("Profile_CopyName", "Desk"));
        copy.Displays.ShouldBe(desk.Displays);
        page.SelectedItem.ShouldNotBeNull().Profile.Id.ShouldBe(copy.Id);
        page.StatusMessage.ShouldBe(Loc.Format("Status_Duplicated", copy.Name));
    });

    [Fact]
    public Task ToggleDefault_SetsAndClearsTheDefaultProfile() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();

        await page.ToggleDefaultCommand.ExecuteAsync(null);
        await UntilAsync(() => page.IsSelectedDefault, "the default was not shown");
        _host.Settings.Current.DefaultProfileId.ShouldBe(desk.Id);

        await page.ToggleDefaultCommand.ExecuteAsync(null);
        await UntilAsync(() => !page.IsSelectedDefault, "the default was not cleared");
        _host.Settings.Current.DefaultProfileId.ShouldBeNull();
    });

    [Fact]
    public Task Delete_Confirmed_RemovesTheProfileWithItsRules_AndSelectsTheNextOne() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        Profile side = await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        await _host.Settings.UpdateAsync(
            s => s with { AutomationRules = [new AutomationRule { Devices = [new RuleDevice { Id = "VID_0EB7&PID_0006" }], ProfileId = desk.Id }] }, Ct);
        ProfilesViewModel page = await PageAsync();
        _dialogs.Delete = true;

        await page.DeleteCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor?.Id == side.Id, "the next profile was not opened");

        _dialogs.DeleteAsked.ShouldHaveSingleItem().ShouldBe(("Desk", 1));
        _host.Store.Profiles.ShouldHaveSingleItem().Id.ShouldBe(side.Id);
        _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldBeEmpty();
        page.StatusMessage.ShouldBe(Loc.Format("Status_Deleted", "Desk"));
    });

    [Fact]
    public Task Delete_NotConfirmed_KeepsTheProfile() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();

        await page.DeleteCommand.ExecuteAsync(null);

        _host.Store.Profiles.ShouldHaveSingleItem();
        page.Editor.ShouldNotBeNull();
    });

    [Fact]
    public Task Test_ShowsWhatASwitchWouldDo_WithoutSwitching() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.TestCommand.CanExecute(null), "the test was not available");

        await page.TestCommand.ExecuteAsync(null);

        page.HasDetailMessage.ShouldBeTrue();
        page.DetailKind.ShouldBe(InfoKind.Info);
        _display.Applied.ShouldBeEmpty();

        page.CloseDetailMessageCommand.Execute(null);
        page.HasDetailMessage.ShouldBeFalse();
    });

    [Fact]
    public Task Switch_AppliesTheProfile() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.CanSwitch, "the profile was not ready");
        page.SwitchIsPrimary.ShouldBeTrue();

        await page.SwitchCommand.ExecuteAsync(null);
        await UntilAsync(() => _display.Applied.Count == 1 && !page.IsBusy, "the switch did not run");

        page.CanSwitch.ShouldBeTrue("the lock goes with the switch");
    });

    [Fact]
    public Task ShowSwitchResult_AFailedSwitch_IsAnErrorInTheDetail() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();

        page.ShowSwitchResult(new SwitchRecord(
            DateTimeOffset.Now, "Desk", SwitchOutcome.Failed, AudioOutcome.NotConfigured, AppsOutcome.NotConfigured, 1, TimeSpan.FromSeconds(1), null, null, []));

        page.DetailKind.ShouldBe(InfoKind.Error);
        page.DetailMessage.ShouldNotBeNull().ShouldContain("Desk");
    });

    public void Dispose()
    {
        _ui.Invoke(() =>
        {
            _watcher.Dispose();
            _hotkeys.Dispose();
            _sessions.Dispose();
        });
        _ui.Dispose();
        _host.Dispose();
    }

    private async Task<Profile> SavedAsync(Profile profile)
    {
        await _host.Catalog.SaveAsync(profile, Ct);
        return profile;
    }

    /// <summary>The page with its first editor loaded; that happens in the background after the constructor.</summary>
    private async Task<ProfilesViewModel> PageAsync()
    {
        var page = new ProfilesViewModel(
            _host.Catalog, _host.Coordinator, _dialogs, _host.Settings, _display, new TopologyPlanner(new TopologyPlannerOptions()), _watcher, Logger.None);
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
    private sealed class Dialogs(ProfileDialogs real) : IProfilePageDialogs
    {
        public UnsavedChoice Unsaved { get; set; } = UnsavedChoice.Cancel;

        public bool Delete { get; set; }

        public List<(string Name, string? Target)> UnsavedAsked { get; } = [];

        public List<(string Name, int Rules)> DeleteAsked { get; } = [];

        public Task<Profile> NewFromCurrentAsync() => real.NewFromCurrentAsync();

        public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew) => real.CreateEditorAsync(profile, isNew);

        public Task ShowSetupAssistantAsync() => Task.CompletedTask;

        public Task<bool> ConfirmDeleteAsync(string name, int ruleCount)
        {
            DeleteAsked.Add((name, ruleCount));
            return Task.FromResult(Delete);
        }

        public Task<UnsavedChoice> ConfirmUnsavedAsync(string name, string? targetName)
        {
            UnsavedAsked.Add((name, targetName));
            return Task.FromResult(Unsaved);
        }
    }
}
