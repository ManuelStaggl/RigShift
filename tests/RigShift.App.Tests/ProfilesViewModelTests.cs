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
    private readonly DisplayChangeWatcher _displayChanges;
    private readonly Dialogs _dialogs;
    private readonly IDesktopIcons _desktopIcons = Substitute.For<IDesktopIcons>();

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

        IAudioController audio = Substitute.For<IAudioController>();
        audio.ListAsync(Arg.Any<AudioDirection>(), Arg.Any<CancellationToken>()).Returns([]);
        ISurroundController surround = Substitute.For<ISurroundController>();
        surround.QueryAsync(Arg.Any<CancellationToken>()).Returns(SurroundState.Unavailable(SurroundAvailability.Unknown));
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        _displayChanges = _ui.Invoke(() => new DisplayChangeWatcher());
        var editorServices = new ProfileEditorServices(
            _host.Catalog, _display, _desktopIcons, _hotkeys, _host.Settings, new FakeAppPicker(), Logger.None);
        _dialogs = new Dialogs(new ProfileDialogs(
            editorServices, audio, _host.Usb, surround, powerCheck,
            new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), _displayChanges, _host.Coordinator));
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
            ("Desk", StatusKind.Active, Loc.Instance["Profile_Active"] + " · " + Loc.Instance["Profile_Default"]),
            ("Side", StatusKind.Ok, Loc.Instance["List_Ready"]),
            ("Rig", StatusKind.Warn, Loc.Format("List_MissingOne", "Ultrawide 49")),
        ]);
        page.IsSelectedActive.ShouldBeTrue();
        page.IsSelectedDefault.ShouldBeTrue();
        page.SwitchIsPrimary.ShouldBeFalse();
        page.SwitchLabel.ShouldBe(Loc.Instance["Profile_Reapply"]);
    });

    /// <summary>
    /// A display that is off does not block: the switch asks for it and waits (K-06). The detail says which one and
    /// offers the two ways out (U-10, U-11).
    /// </summary>
    [Fact]
    public Task ProfileWithADisplayOff_CanStillSwitch_AndTheHeadNamesWhatIsMissing() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Rig());

        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.HasBlockedMessage, "the missing display was not reported");

        page.CanSwitch.ShouldBeTrue();
        page.BlockedMessage.ShouldNotBeNull().ShouldContain("Ultrawide 49");
        page.BlockedKind.ShouldBe(InfoKind.Warn);
        page.CanMarkOptional.ShouldBeFalse("the missing display is the main one, which cannot be optional");
        page.HeadStatusKind.ShouldBe(StatusKind.Warn);
        page.HeadStatusText.ShouldBe(Loc.Instance["Head_MissingOne"]);
        page.Editor.ShouldNotBeNull().TopologyDisplays.Count(d => d.State == TopologyDisplayState.Missing).ShouldBe(1);
    });

    [Fact]
    public Task MarkMissingOptional_MakesTheMissingDisplaysOptional_ButNotThePrimary() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Wide", [Mode(Desk4K, 3840, 2160, 165, primary: true), Mode(Ultrawide, 5120, 1440, 240, x: 3840)]));
        ProfilesViewModel page = await PageAsync();
        await UntilAsync(() => page.HasBlockedMessage, "the missing display was not reported");
        page.CanMarkOptional.ShouldBeTrue();

        page.MarkMissingOptionalCommand.Execute(null);

        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Displays.Single(d => !d.IsPrimary).IsOptional.ShouldBeTrue();
        editor.Displays.Single(d => d.IsPrimary).IsOptional.ShouldBeFalse();
        editor.IsDirty.ShouldBeTrue("the change waits for Save like any other");
    });

    /// <summary>The overview and the tray read the same state from the catalog's items (U-10).</summary>
    [Fact]
    public Task CatalogItems_CarryTheReadinessForOverviewAndTray() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        await SavedAsync(Rig());
        await _host.Catalog.RefreshActiveAsync(Ct);

        ProfileItem desk = _host.Catalog.Items.Single(i => i.Name == "Desk");
        ProfileItem rig = _host.Catalog.Items.Single(i => i.Name == "Rig");
        desk.ShowsReadiness.ShouldBeFalse("the active profile shows Active, not a readiness line");
        rig.ShowsReadiness.ShouldBeTrue();
        rig.ShowsHotkeyLine.ShouldBeFalse();
        rig.ReadyKind.ShouldBe(StatusKind.Warn);
        rig.ReadyText.ShouldBe(Loc.Format("List_MissingOne", "Ultrawide 49"));
        rig.ReadyTip.ShouldNotBeNull().ShouldContain("Ultrawide 49");
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
        page.DetailMessage.ShouldBe(Loc.Format("Detail_Renamed", "Desk"), "links and keys with the old name stop working (U-12)");
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
    public Task SwitchResult_LandsInTheDetail_WithoutTheTray() => _ui.RunAsync(async () =>
    {
        // A-06: the tray used to hand every result to the page, and so built the page before the tray icon appeared.
        Profile rig = await SavedAsync(Rig());
        ProfilesViewModel page = await PageAsync();

        (await _host.Coordinator.SwitchAsync(rig, SwitchRequest.Default)).ShouldNotBeNull().Outcome.ShouldBe(SwitchOutcome.Blocked);

        page.DetailKind.ShouldBe(InfoKind.Warn);
        page.DetailMessage.ShouldNotBeNull().ShouldContain("Rig");
    });

    /// <summary>A-06: the first editor reads audio and USB devices and Surround; nobody looks at it before the page is shown.</summary>
    [Fact]
    public Task NewPage_OpensNoEditorUntilItIsShown() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        var page = new ProfilesViewModel(
            _host.Catalog, _host.Coordinator, _dialogs, _host.Settings, _display, new TopologyPlanner(new TopologyPlannerOptions()), _desktopIcons, Logger.None);

        page.SelectedItem.ShouldNotBeNull().Name.ShouldBe("Desk");
        _dialogs.EditorsCreated.ShouldBe(0);

        page.PageShown();
        await UntilAsync(() => page.Editor is not null, "the editor did not open once the page was shown");
        _dialogs.EditorsCreated.ShouldBe(1);
    });

    [Fact]
    public Task RestoreDesktopIcons_PutsTheSavedIconsBack_WithoutSwitching() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes) with { DesktopIcons = Icons });
        _desktopIcons.Restore(Arg.Any<DesktopIconLayout>()).Returns(new DesktopIconResult(DesktopIconOutcome.Restored, 2, 0));
        ProfilesViewModel page = await PageAsync();

        page.RestoreDesktopIconsCommand.CanExecute(null).ShouldBeTrue();
        await page.RestoreDesktopIconsCommand.ExecuteAsync(null);

        _desktopIcons.Received(1).Restore(Arg.Is<DesktopIconLayout>(l => l.Icons.Count == 2));
        _display.Applied.ShouldBeEmpty();
        page.StatusMessage.ShouldBe(Loc.Format("Status_IconsRestored", 2));
        page.HasDetailMessage.ShouldBeFalse();
    });

    [Fact]
    public Task RestoreDesktopIcons_IconsThatAreGone_AreCountedInAWarning() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes) with { DesktopIcons = Icons });
        _desktopIcons.Restore(Arg.Any<DesktopIconLayout>()).Returns(new DesktopIconResult(DesktopIconOutcome.Restored, 1, 1));
        ProfilesViewModel page = await PageAsync();

        await page.RestoreDesktopIconsCommand.ExecuteAsync(null);

        page.DetailKind.ShouldBe(InfoKind.Warn);
        page.DetailMessage.ShouldBe(Loc.Format("Status_IconsRestoredMissing", 1, 1));
    });

    [Theory]
    [InlineData(DesktopIconOutcome.AutoArrange, "Status_IconsAutoArrange")]
    [InlineData(DesktopIconOutcome.Unavailable, "Status_IconsUnavailable")]
    public Task RestoreDesktopIcons_WhenTheDesktopRefuses_SaysWhy(DesktopIconOutcome outcome, string key) => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes) with { DesktopIcons = Icons });
        _desktopIcons.Restore(Arg.Any<DesktopIconLayout>()).Returns(new DesktopIconResult(outcome, 0, 0));
        ProfilesViewModel page = await PageAsync();

        await page.RestoreDesktopIconsCommand.ExecuteAsync(null);

        page.DetailKind.ShouldBe(InfoKind.Error);
        page.DetailMessage.ShouldBe(Loc.Instance[key]);
    });

    [Fact]
    public Task RestoreDesktopIcons_FollowsTheEditor_NotOnlyTheSavedProfile() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        _desktopIcons.Restore(Arg.Any<DesktopIconLayout>()).Returns(new DesktopIconResult(DesktopIconOutcome.Restored, 2, 0));
        ProfilesViewModel page = await PageAsync();
        page.RestoreDesktopIconsCommand.CanExecute(null).ShouldBeFalse("nothing is saved yet");

        _desktopIcons.Capture().Returns(Icons);
        await page.Editor.ShouldNotBeNull().CaptureDesktopIconsCommand.ExecuteAsync(null);

        page.RestoreDesktopIconsCommand.CanExecute(null).ShouldBeTrue("what the row shows is what the button restores");

        page.Editor.ClearDesktopIconsCommand.Execute(null);
        page.RestoreDesktopIconsCommand.CanExecute(null).ShouldBeFalse();
    });

    /// <summary>Windows rearranged the displays while the profile was being edited (v4 finding A-01).</summary>
    [Fact]
    public Task DirtyEditor_SurvivesADisplayChange() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";
        int created = _dialogs.EditorsCreated;

        await _host.Catalog.RefreshActiveAsync(Ct);

        _dialogs.EditorsCreated.ShouldBe(created);
        page.Editor.ShouldBeSameAs(editor);
        editor.Name.ShouldBe("Desk 2");
        page.IsStale.ShouldBeFalse();
    });

    [Fact]
    public Task DirtyEditor_SurvivesASwitch() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        Profile side = await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";
        int created = _dialogs.EditorsCreated;

        await _host.Coordinator.SwitchAsync(side);

        _dialogs.EditorsCreated.ShouldBe(created);
        page.Editor.ShouldBeSameAs(editor);
        editor.IsDirty.ShouldBeTrue();
    });

    /// <summary>Another profile saved elsewhere (the tray, the command line) reloads the whole list.</summary>
    [Fact]
    public Task DirtyEditor_SurvivesAnotherProfileBeingSaved() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";
        int created = _dialogs.EditorsCreated;

        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));

        page.Items.Count.ShouldBe(2);
        _dialogs.EditorsCreated.ShouldBe(created);
        page.Editor.ShouldBeSameAs(editor);
        page.SelectedItem.ShouldNotBeNull().Id.ShouldBe(editor.Id);
        page.IsStale.ShouldBeFalse();
    });

    [Fact]
    public Task NewUnsavedProfile_SurvivesAnotherProfileBeingSaved() => _ui.RunAsync(async () =>
    {
        ProfilesViewModel page = await PageAsync();
        await page.NewFromCurrentCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { IsNew: true }, "the new profile's editor did not open");
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Rig";

        await SavedAsync(Profile("Side", [Mode(DeskLeft, 1920, 1080, 100, primary: true)]));

        page.Items.Count.ShouldBe(2);
        page.Items[0].IsNew.ShouldBeTrue();
        page.SelectedItem.ShouldBeSameAs(page.Items[0]);
        page.Editor.ShouldBeSameAs(editor);
        editor.Name.ShouldBe("Rig");
    });

    /// <summary>Unsaved changes stay; the bar says the profile changed on disk and offers the saved version.</summary>
    [Fact]
    public Task DirtyEditor_ProfileChangedOnDisk_OffersAReload() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";

        await SavedAsync(desk with { Name = "Desk (renamed elsewhere)" });

        page.Editor.ShouldBeSameAs(editor);
        page.IsStale.ShouldBeTrue();

        await page.ReloadCommand.ExecuteAsync(null);
        await UntilAsync(() => page.Editor is { } fresh && fresh != editor, "the saved version was not loaded");

        page.Editor.ShouldNotBeNull().Name.ShouldBe("Desk (renamed elsewhere)");
        page.Editor.IsDirty.ShouldBeFalse();
        page.IsStale.ShouldBeFalse();
    });

    [Fact]
    public Task CleanEditor_ProfileChangedOnDisk_ShowsTheNewVersion() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();

        await SavedAsync(desk with { Name = "Desk (renamed elsewhere)" });
        await UntilAsync(() => page.Editor is { } fresh && fresh != editor, "the editor was not reloaded");

        page.Editor.ShouldNotBeNull().Name.ShouldBe("Desk (renamed elsewhere)");
        page.IsStale.ShouldBeFalse();
    });

    /// <summary>The assistant added a USB rule for the profile that is open: a clean editor shows it (v4 finding A-02).</summary>
    [Fact]
    public Task CleanEditor_ItsRulesChangedOnDisk_ShowsThem() => _ui.RunAsync(async () =>
    {
        Profile desk = await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Rules.Rules.ShouldBeEmpty();

        var rule = new AutomationRule { ProfileId = desk.Id, Devices = [new RuleDevice { Id = "VID_0EB7&PID_0020" }] };
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = [rule] }, Ct);
        await UntilAsync(() => page.Editor is { } fresh && fresh != editor, "the editor was not reloaded");

        page.Editor.ShouldNotBeNull().Rules.Rules.ShouldHaveSingleItem();
        page.Editor.IsDirty.ShouldBeFalse();
    });

    /// <summary>Saving keeps the editor: its tab and scroll position stay where the user left them.</summary>
    [Fact]
    public Task Save_KeepsTheEditor() => _ui.RunAsync(async () =>
    {
        await SavedAsync(Profile("Desk", DeskModes));
        ProfilesViewModel page = await PageAsync();
        ProfileEditorViewModel editor = page.Editor.ShouldNotBeNull();
        editor.Name = "Desk 2";
        int created = _dialogs.EditorsCreated;

        await page.SaveCommand.ExecuteAsync(null);

        page.Editor.ShouldBeSameAs(editor);
        editor.IsDirty.ShouldBeFalse();
        _dialogs.EditorsCreated.ShouldBe(created);
        page.IsStale.ShouldBeFalse();
        page.SelectedItem.ShouldNotBeNull().Name.ShouldBe("Desk 2");
    });

    private static DesktopIconLayout Icons => new()
    {
        CapturedAt = DateTimeOffset.UnixEpoch,
        Icons =
        [
            new DesktopIcon { Item = @"C:\Users\x\Desktop\a.lnk", X = 10, Y = 20 },
            new DesktopIcon { Item = "::{645FF040-5081-101B-9F08-00AA002F954E}", X = 10, Y = 120 },
        ],
    };

    public void Dispose()
    {
        _ui.Invoke(() =>
        {
            _hotkeys.Dispose();
            _sessions.Dispose();
            _displayChanges.Dispose();
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
            _host.Catalog, _host.Coordinator, _dialogs, _host.Settings, _display, new TopologyPlanner(new TopologyPlannerOptions()), _desktopIcons, Logger.None);
        page.PageShown();
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

        /// <summary>How many editors the page asked for; counted when asked, before the editor is ready.</summary>
        public int EditorsCreated { get; private set; }

        public Task<ProfileEditorViewModel> CreateEditorAsync(Profile profile, bool isNew)
        {
            EditorsCreated++;
            return real.CreateEditorAsync(profile, isNew);
        }

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
