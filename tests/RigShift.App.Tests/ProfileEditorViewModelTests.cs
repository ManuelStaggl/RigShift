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

/// <summary>The profile detail without its page: what it shows for a stored profile, what it validates, what it writes.</summary>
public sealed class ProfileEditorViewModelTests : IDisposable
{
    private const string Wheelbase = "VID_0EB7&PID_0006";

    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };
    private static readonly AudioEndpoint Speakers = new("{render-1}", "Speakers");
    private static readonly AudioEndpoint Headset = new("{render-2}", "Headset");
    private static readonly AudioEndpoint Microphone = new("{capture-1}", "Microphone");
    private static readonly RefreshRate Hz60 = new(60, 1);
    private static readonly RefreshRate Hz120 = new(120, 1);

    private static readonly SurroundGrid Triple = new()
    {
        Rows = 1,
        Columns = 3,
        Width = 2560,
        Height = 1440,
        Displays = [new SurroundDisplay { DisplayId = 1 }, new SurroundDisplay { DisplayId = 2 }, new SurroundDisplay { DisplayId = 3 }],
    };

    private readonly FakeDisplayConfigurator _display = new(DeskActive());
    private readonly AppTestHost _host;
    private readonly InMemoryGameStore _games = new();
    private readonly GameCatalog _gameCatalog;
    private readonly GameSessionService _sessions;
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly HotkeyService _hotkeys;
    private readonly IDesktopIcons _desktopIcons = Substitute.For<IDesktopIcons>();
    private readonly FakeAppPicker _picker = new();
    private readonly List<ProfileEditorViewModel> _editors = [];

    public ProfileEditorViewModelTests()
    {
        _display.RefreshRates.AddRange([Hz60, Hz120]);
        _host = new AppTestHost(_display);
        _gameCatalog = new GameCatalog(_games, _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _gameCatalog,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        _hotkeys = new HotkeyService(_host.Catalog, _gameCatalog, _sessions, _host.Coordinator, _host.Settings, Logger.None, _registrar);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task StoredProfile_OpensCleanAndBuildsItselfBack()
    {
        Profile rig = Rig(audio: new AudioAssignment
        {
            Playback = Headset,
            PlaybackVolumePercent = 40,
            Recording = Microphone,
            PlaybackCommunications = Speakers,
        }) with
        {
            Icon = ProfileIcons.Desk,
            Hotkey = CtrlAltR,
            Apps = [new AppAction { Path = @"C:\Tools\SimHub.exe", Name = "SimHub", WaitSeconds = 5 }],
            AppsWaitForUsbDeviceId = Wheelbase,
            AppsWaitForUsbDeviceName = "Fanatec Wheelbase",
            KeepAwake = true,
            DisableCommunicationsDucking = true,
            Surround = new SurroundSetting { Enabled = true, Grid = Triple },
        };

        ProfileEditorViewModel editor = await EditorAsync(rig);

        editor.IsDirty.ShouldBeFalse();
        editor.ProblemCount.ShouldBe(0);
        editor.ShowCommunicationsAudio.ShouldBeTrue();
        editor.Surround.IsVisible.ShouldBeTrue("a setting from another machine must stay visible");
        await SaveAsync(editor, expected: true);
        Profile saved = _host.Store.Profiles.ShouldHaveSingleItem();
        saved.Displays.ShouldBe(rig.Displays);
        saved.Apps.ShouldBe(rig.Apps);
        (saved with { Displays = rig.Displays, Apps = rig.Apps }).ShouldBe(rig);
    }

    [Fact]
    public async Task NewProfile_IsDirtyBeforeAnythingIsTyped_AndCleanAfterTheFirstSave()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig(), isNew: true);
        editor.IsDirty.ShouldBeTrue();

        await SaveAsync(editor, expected: true);

        editor.IsNew.ShouldBeFalse();
        editor.IsDirty.ShouldBeFalse();
        _host.Store.Profiles.ShouldHaveSingleItem().Name.ShouldBe("Rig");
    }

    [Fact]
    public async Task Change_MakesDirty_AndChangingBackMakesCleanAgain()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.KeepAwake = true;
        editor.IsDirty.ShouldBeTrue();

        editor.KeepAwake = false;
        editor.Name = " Rig ";
        editor.IsDirty.ShouldBeFalse();
    }

    /// <summary>Every field counts for the save bar: a hand-kept list of names used to decide, and a field missing from it lost changes.</summary>
    [Fact]
    public async Task EveryField_MakesDirty()
    {
        var surround = new SurroundState { Availability = SurroundAvailability.Available, Grids = [Triple] };
        var icons = new DesktopIconLayout { CapturedAt = DateTimeOffset.UtcNow, Icons = [new DesktopIcon { Item = @"C:\Users\x\Desktop\a.lnk", X = 10, Y = 20 }] };
        Action<ProfileEditorViewModel>[] changes =
        [
            e => e.Name = "Rig 2",
            e => e.SelectedIcon = e.IconChoices.First(c => c.Key != e.SelectedIcon?.Key),
            e => e.SwitchWithoutAsking = !e.SwitchWithoutAsking,
            e => e.Hotkey = CtrlAltR,
            e => e.KeepAwake = true,
            e => e.DisableCommunicationsDucking = true,
            e => e.DesktopIcons = icons,
            e => e.Surround.Selected = e.Surround.Choices.First(c => c.Key == "off"),
        ];

        for (int i = 0; i < changes.Length; i++)
        {
            ProfileEditorViewModel editor = await EditorAsync(Rig(), surround: surround);
            changes[i](editor);
            editor.IsDirty.ShouldBeTrue($"change {i}");
        }
    }

    [Fact]
    public async Task NoDisplayLeft_IsAProblemWithItsOwnText()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        foreach (DisplayEditItem display in editor.Displays.ToList())
        {
            editor.RemoveDisplayCommand.Execute(display);
        }

        editor.DisplaysProblem.ShouldBe(Loc.Instance["Problem_NoDisplays"]);
        editor.ProblemCount.ShouldBe(1);
    }

    [Fact]
    public async Task Name_MissingOrTakenByAnotherProfile_IsAProblem()
    {
        await _host.Catalog.SaveAsync(Profile("Desk", DeskModes), Ct);
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.Name = " ";
        editor.NameProblem.ShouldBe(Loc.Instance["Problem_NameMissing"]);

        editor.Name = "desk";
        editor.NameProblem.ShouldBe(Loc.Instance["Problem_NameTaken"]);
        editor.ProblemCount.ShouldBe(1);
        await SaveAsync(editor, expected: false);
        _host.Store.Profiles.ShouldHaveSingleItem().Name.ShouldBe("Desk");
    }

    [Fact]
    public async Task HotkeyOfAnotherProfile_IsAProblem()
    {
        await _host.Catalog.SaveAsync(Profile("Desk", DeskModes) with { Hotkey = CtrlAltD }, Ct);
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.Hotkey = CtrlAltD;

        editor.HotkeyProblem.ShouldBe(Loc.Instance["Problem_HotkeyTaken"]);
    }

    [Fact]
    public async Task RecordHotkey_WithoutModifierOrHeldElsewhere_OnlyShowsAHint()
    {
        _registrar.TakenElsewhere.Add(CtrlAltD);
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.RecordHotkey(HotkeyModifiers.None, 0x52);
        editor.HotkeyHint.ShouldBe(Loc.Instance["Editor_HotkeyNeedsModifier"]);

        editor.RecordHotkey(CtrlAltD.Modifiers, CtrlAltD.VirtualKey);
        editor.HotkeyHint.ShouldBe(Loc.Instance["Problem_HotkeyInUse"]);

        editor.Hotkey.ShouldBeNull();
        editor.HotkeyText.ShouldBe(Loc.Instance["Editor_HotkeyPlaceholder"]);
    }

    [Fact]
    public async Task RecordHotkey_HeldByAGame_NamesTheGame()
    {
        await _gameCatalog.SaveAsync(
            new GameEntry
            {
                Id = Guid.NewGuid(),
                Name = "iRacing",
                Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
                Hotkey = CtrlAltR,
            },
            Ct);
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);

        editor.Hotkey.ShouldBeNull();
        editor.HotkeyHint.ShouldContain("iRacing");
    }

    [Fact]
    public async Task RecordHotkey_FreeCombination_IsTaken_AndClearRemovesIt()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);

        editor.Hotkey.ShouldBe(CtrlAltR);
        editor.HasHotkey.ShouldBeTrue();
        editor.IsDirty.ShouldBeTrue();

        editor.ClearHotkeyCommand.Execute(null);
        editor.Hotkey.ShouldBeNull();
        editor.IsDirty.ShouldBeFalse();
    }

    /// <summary>A hotkey on a PC where RigShift does not start with Windows stops working after a restart (U-01).</summary>
    [Fact]
    public async Task Hotkey_WhileNotStartingWithWindows_ShowsTheNote_AndTurnOnFixesIt()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());
        editor.ShowAutostartOff.ShouldBeFalse("nothing needs RigShift running yet");

        editor.RecordHotkey(CtrlAltR.Modifiers, CtrlAltR.VirtualKey);
        editor.ShowAutostartOff.ShouldBeTrue();

        _host.Settings.Autostart.IsEnabled.Returns(true);
        editor.TurnOnAutostartCommand.Execute(null);

        _host.Settings.Autostart.Received(1).SetEnabled(true);
        editor.ShowAutostartOff.ShouldBeFalse();
    }

    [Fact]
    public async Task MakingAnotherDisplayPrimary_MovesTheOriginThere_AndTheOldPrimaryLosesTheFlag()
    {
        ProfileEditorViewModel editor = await EditorAsync(Profile("Desk", DeskModes));
        DisplayEditItem left = editor.Displays[1];

        left.IsPrimary = true;

        editor.Displays.Count(d => d.IsPrimary).ShouldBe(1);
        left.Assignment.PositionX.ShouldBe(0);
        left.CanBeOptional.ShouldBeFalse();
        editor.Displays[0].Assignment.PositionX.ShouldBe(1920);
        editor.HasDisplaysProblem.ShouldBeFalse();
        editor.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task RemovingThePrimaryDisplay_IsAProblem_AndTheSelectionMovesOn()
    {
        ProfileEditorViewModel editor = await EditorAsync(Profile("Desk", DeskModes));
        editor.SelectedDisplay.ShouldBe(editor.Displays[0]);

        editor.RemoveDisplayCommand.Execute(editor.Displays[0]);

        editor.Displays.Count.ShouldBe(2);
        editor.SelectedDisplay.ShouldBeNull();
        editor.DisplaysProblem.ShouldBe(Loc.Instance["Problem_NoSinglePrimary"]);
        editor.TopologyDisplays.Count.ShouldBe(2);
    }

    [Fact]
    public async Task DisplayProperties_NameRateHdrAndOptional_EndUpInTheSavedProfile()
    {
        ProfileEditorViewModel editor = await EditorAsync(Profile("Desk", DeskModes));
        DisplayEditItem right = editor.Displays[2];

        right.CustomName = "  Telemetry ";
        right.SelectedRefresh = right.RefreshChoices.First(c => c.Rate == Hz60);
        right.SelectedHdr = right.HdrChoices.First(c => c.Value == false);
        right.IsOptional = true;

        right.SwitchesHdr.ShouldBeTrue();
        await SaveAsync(editor, expected: true);
        DisplayAssignment saved = _host.Store.Profiles.ShouldHaveSingleItem().Displays[2];
        saved.CustomName.ShouldBe("Telemetry");
        RefreshRate.Of(saved).ShouldBe(Hz60);
        saved.Hdr.ShouldBe(false);
        saved.IsOptional.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshRates_TheSavedOneStaysAndTheDisplaysOwnAreOffered()
    {
        ProfileEditorViewModel editor = await EditorAsync(Profile("Desk", DeskModes));

        DisplayEditItem main = editor.Displays[0];

        main.RatesUnknown.ShouldBeFalse();
        main.RefreshChoices.Select(c => c.Rate.Hertz).ShouldBe([165, 120, 60]);
        main.SelectedRefresh.ShouldNotBeNull().Rate.Hertz.ShouldBe(165);
        editor.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task TakeCurrent_ReplacesTheDisplaysWithWhatIsOnScreen()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        await editor.TakeCurrentCommand.ExecuteAsync(null);
        await RatesLoadedAsync(editor);

        editor.Displays.Select(d => d.Key).ShouldBe(
            [DeskLeft.TargetDevicePath, Desk4K.TargetDevicePath, DeskRight.TargetDevicePath], "left to right");
        editor.ArrangementNote.ShouldBe(Loc.Format("Editor_Taken", 3));
        editor.SelectedDisplay.ShouldNotBeNull().IsPrimary.ShouldBeTrue();
        editor.IsDirty.ShouldBeTrue();
        editor.ProblemCount.ShouldBe(0);
    }

    [Fact]
    public async Task ShowPlan_MarksMissingDisplaysInThePicture_AndNullClearsThemAgain()
    {
        Profile rig = Rig();
        ProfileEditorViewModel editor = await EditorAsync(rig);
        var planner = new TopologyPlanner(new TopologyPlannerOptions());

        editor.ShowPlan(planner.Plan(rig, DeskActive(ultrawideAvailable: false)));
        editor.TopologyDisplays.Count(d => d.State == TopologyDisplayState.Missing).ShouldBe(1);

        editor.ShowPlan(null);
        editor.TopologyDisplays.ShouldAllBe(d => d.State != TopologyDisplayState.Missing);
        editor.HasWarning.ShouldBeFalse();
    }

    [Fact]
    public async Task Audio_DeviceAndVolume_AreSaved_AndUnchangedMeansNull()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());
        AudioSlot playback = editor.AudioSlots[0];

        playback.Selected = playback.Choices.First(c => c.Endpoint == Headset);
        editor.IsDirty.ShouldBeTrue();

        await SaveAsync(editor, expected: true);
        AudioAssignment saved = _host.Store.Profiles.ShouldHaveSingleItem().Audio;
        saved.Playback.ShouldBe(Headset);
        saved.Recording.ShouldBeNull();
        saved.PlaybackCommunications.ShouldBeNull();
        editor.ShowCommunicationsAudio.ShouldBeFalse();
    }

    [Fact]
    public async Task Apps_EmptyPathIsAProblem_AndTheOrderIsSaved()
    {
        Profile rig = Rig() with { Apps = [new AppAction { Path = @"C:\a.exe" }, new AppAction { Path = @"C:\b.exe" }] };
        ProfileEditorViewModel editor = await EditorAsync(rig);

        editor.AppList.Add(string.Empty);
        editor.AppsProblem.ShouldBe(Loc.Instance["Problem_AppPathMissing"]);
        editor.AppList.Problem.ShouldBe(editor.AppsProblem, "the list shows it above the cards");

        editor.AppList.RemoveCommand.Execute(editor.AppList.Items[2]);
        editor.AppList.MoveDownCommand.Execute(editor.AppList.Items[0]);
        editor.HasAppsProblem.ShouldBeFalse();

        await SaveAsync(editor, expected: true);
        _host.Store.Profiles.ShouldHaveSingleItem().Apps.Select(a => a.Path).ShouldBe([@"C:\b.exe", @"C:\a.exe"]);
    }

    [Fact]
    public async Task AppsWaitDevice_SavedButNotConnected_IsStillOffered_AndKeepsItsName()
    {
        Profile rig = Rig() with { AppsWaitForUsbDeviceId = "VID_1111&PID_2222", AppsWaitForUsbDeviceName = "Pedals" };

        ProfileEditorViewModel editor = await EditorAsync(rig);

        editor.AppList.WaitDevice.Selected.ShouldNotBeNull().Key.ShouldBe("VID_1111&PID_2222");
        editor.AppList.WaitDevice.Selected.Name.ShouldContain("Pedals");
        editor.IsDirty.ShouldBeFalse();

        editor.AppList.WaitDevice.Selected = editor.AppList.WaitDevice.Choices.First(c => c.Key == Wheelbase);
        await SaveAsync(editor, expected: true);
        Profile saved = _host.Store.Profiles.ShouldHaveSingleItem();
        saved.AppsWaitForUsbDeviceId.ShouldBe(Wheelbase);
        saved.AppsWaitForUsbDeviceName.ShouldBe("Fanatec Wheelbase");
    }

    [Fact]
    public async Task DesktopIcons_CaptureTakesWhatTheDesktopReports_AndNothingWhenItReportsNone()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());
        _desktopIcons.Capture().Returns((DesktopIconLayout?)null);

        await editor.CaptureDesktopIconsCommand.ExecuteAsync(null);
        editor.HasDesktopIcons.ShouldBeFalse();
        editor.DesktopIconsText.ShouldBe(Loc.Instance["Status_IconsUnavailable"]);

        var layout = new DesktopIconLayout { CapturedAt = DateTimeOffset.UtcNow, Icons = [new DesktopIcon { Item = @"C:\Users\x\Desktop\a.lnk", X = 10, Y = 20 }] };
        _desktopIcons.Capture().Returns(layout);
        await editor.CaptureDesktopIconsCommand.ExecuteAsync(null);

        editor.HasDesktopIcons.ShouldBeTrue();
        editor.DesktopIconsText.ShouldNotBe(Loc.Instance["Status_IconsUnavailable"]);
        editor.IsDirty.ShouldBeTrue();

        editor.ClearDesktopIconsCommand.Execute(null);
        editor.IsDirty.ShouldBeFalse();
    }

    [Fact]
    public async Task Surround_WithoutNvidiaAndWithoutASavedSetting_IsNotShown()
    {
        ProfileEditorViewModel editor = await EditorAsync(Rig());

        editor.Surround.IsVisible.ShouldBeFalse();
        editor.Surround.Choices.ShouldBeEmpty();
    }

    [Fact]
    public async Task Surround_OnWithAGrid_IsSavedWithThatGrid_AndOffWithoutOne()
    {
        var state = new SurroundState { Availability = SurroundAvailability.Available, Grids = [Triple] };
        ProfileEditorViewModel editor = await EditorAsync(Rig(), surround: state);
        editor.Surround.Selected.ShouldNotBeNull().Key.ShouldBe("unchanged");

        editor.Surround.Selected = editor.Surround.Choices.First(c => c.Key == "on");
        editor.Surround.HintIsError.ShouldBeFalse();
        await SaveAsync(editor, expected: true);
        _host.Store.Profiles.ShouldHaveSingleItem().Surround.ShouldBe(new SurroundSetting { Enabled = true, Grid = Triple });

        editor.Surround.Selected = editor.Surround.Choices.First(c => c.Key == "off");
        await SaveAsync(editor, expected: true);
        _host.Store.Profiles.ShouldHaveSingleItem().Surround.ShouldBe(new SurroundSetting { Enabled = false });
    }

    [Fact]
    public async Task Surround_OnWithoutAnyGrid_ShowsTheHintAsAnError_AndSavesNoSetting()
    {
        var state = new SurroundState { Availability = SurroundAvailability.Available };
        ProfileEditorViewModel editor = await EditorAsync(Rig(), surround: state);

        editor.Surround.Selected = editor.Surround.Choices.First(c => c.Key == "on");

        editor.Surround.HintIsError.ShouldBeTrue();
        editor.Surround.Hint.ShouldBe(Loc.Instance["Editor_SurroundNoGrid"]);
        editor.IsDirty.ShouldBeFalse("there is nothing to switch on, so nothing changed");
    }

    [Fact]
    public async Task Rules_AreSavedWithTheProfile()
    {
        Profile rig = Rig();
        ProfileEditorViewModel editor = await EditorAsync(rig);

        editor.Rules.AddRuleCommand.Execute(null);
        editor.IsDirty.ShouldBeTrue();

        await SaveAsync(editor, expected: true);

        editor.IsDirty.ShouldBeFalse();
        _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem().ProfileId.ShouldBe(rig.Id);
    }

    [Fact]
    public async Task Rules_Save_KeepsARuleAnotherProfileGotWhileTheEditorWasOpen()
    {
        Profile rig = Rig();
        Guid desk = Guid.NewGuid();
        ProfileEditorViewModel editor = await EditorAsync(rig);
        var added = new AutomationRule { ProfileId = desk, Devices = [new RuleDevice { Id = "VID_046D&PID_C547" }] };
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = [added] }, TestContext.Current.CancellationToken);

        editor.Rules.AddRuleCommand.Execute(null);
        await SaveAsync(editor, expected: true);

        _host.Settings.Current.AutomationRules.ShouldNotBeNull().Select(r => r.ProfileId).ShouldBe([desk, rig.Id]);
    }

    [Fact]
    public async Task Save_DiskSaysNo_ShowsTheError_AndTheProfileStaysNewAndDirty()
    {
        IProfileStore store = Substitute.For<IProfileStore>();
        store.SaveAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full", unchecked((int)0x80070070)));
        var catalog = new ProfileCatalog(
            store, _display, new ActiveProfileMatcher(new TopologyPlanner(new TopologyPlannerOptions())), _host.Settings, _host.Paths, Logger.None);
        ProfileEditorViewModel editor = await EditorAsync(Rig(), isNew: true, catalog: catalog);

        await SaveAsync(editor, expected: false);

        editor.ErrorMessage.ShouldBe(Loc.Instance["Error_DiskFull"]);
        editor.IsNew.ShouldBeTrue();
        editor.IsDirty.ShouldBeTrue();
    }

    [Fact]
    public async Task CommandText_EscapesTheName()
    {
        ProfileEditorViewModel editor = await EditorAsync(Profile("Sim Rig", DeskModes));

        editor.CommandText.ShouldBe("rigshift://apply/Sim%20Rig");
    }

    public void Dispose()
    {
        foreach (ProfileEditorViewModel editor in _editors)
        {
            editor.Dispose();
        }

        _hotkeys.Dispose();
        _sessions.Dispose();
        _host.Dispose();
    }

    private static async Task SaveAsync(ProfileEditorViewModel editor, bool expected) =>
        (await editor.SaveAsync()).ShouldBe(expected);

    private async Task<ProfileEditorViewModel> EditorAsync(
        Profile profile, bool isNew = false, SurroundState? surround = null, ProfileCatalog? catalog = null)
    {
        IReadOnlyList<UsbDevice> connected = [new UsbDevice(Wheelbase, "Fanatec Wheelbase")];
        IUsbPowerCheck powerCheck = Substitute.For<IUsbPowerCheck>();
        powerCheck.Check(Arg.Any<string>()).Returns(new UsbPowerFindings());
        var rules = new ProfileRulesEditor(profile.Id, [], [profile], null, connected, null, powerCheck, Logger.None);
        var context = new ProfileEditorContext(
            [new AudioDeviceInfo(Speakers, AudioDirection.Render, true, AudioRoleMask.Console), new AudioDeviceInfo(Headset, AudioDirection.Render, true, 0)],
            [new AudioDeviceInfo(Microphone, AudioDirection.Capture, true, AudioRoleMask.Console)],
            new AppsWaitDeviceChoice(profile.AppsWaitForUsbDeviceId, profile.AppsWaitForUsbDeviceName, connected, [], null),
            surround ?? SurroundState.Unavailable(SurroundAvailability.Unknown),
            rules,
            ConfirmationEnabled: true);
        var services = new ProfileEditorServices(
            catalog ?? _host.Catalog, _display, _desktopIcons, _hotkeys, _host.Settings, _picker, Logger.None);
        var editor = new ProfileEditorViewModel(profile, isNew, context, services);
        _editors.Add(editor);
        await RatesLoadedAsync(editor);
        return editor;
    }

    /// <summary>
    /// The editor asks each display for its rates in the background and fills the lists from there; remembering them
    /// is its last step. A test that touches a display before that is done would race it.
    /// </summary>
    private async Task RatesLoadedAsync(ProfileEditorViewModel editor)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            if (editor.Displays.All(d => _host.Catalog.RememberedRefreshRates(d.Assignment.Identity, d.Assignment.Width, d.Assignment.Height).Count > 0))
            {
                return;
            }

            await Task.Delay(10, Ct);
        }

        throw new TimeoutException("The refresh rates were not offered.");
    }
}
