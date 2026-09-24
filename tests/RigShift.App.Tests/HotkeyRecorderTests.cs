using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using NSubstitute;
using RigShift.App.Controls;
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

/// <summary>The hotkey rules of the profile editor, the game editor and the settings, and the field that feeds them.</summary>
public sealed class HotkeyRecorderTests : IDisposable
{
    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly GameCatalog _games;
    private readonly GameSessionService _sessions;
    private readonly FakeHotkeyRegistrar _registrar = new();
    private readonly HotkeyService _hotkeys;

    public HotkeyRecorderTests()
    {
        _games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _games,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        _hotkeys = new HotkeyService(_host.Catalog, _games, _sessions, _host.Coordinator, _host.Settings, Logger.None, _registrar);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Record_WithoutCtrlAltOrWin_IsRefusedWithAHint()
    {
        var recorder = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Profile, Guid.NewGuid());

        recorder.Record(HotkeyModifiers.Shift, 0x52).ShouldBeNull();

        recorder.Hint.ShouldBe(Loc.Instance["Editor_HotkeyNeedsModifier"]);
    }

    [Fact]
    public void Record_HeldByAnotherApplication_IsRefused()
    {
        _registrar.TakenElsewhere.Add(CtrlAltR);
        var recorder = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Game, Guid.NewGuid());

        recorder.Record(CtrlAltR.Modifiers, CtrlAltR.VirtualKey).ShouldBeNull();

        recorder.Hint.ShouldBe(Loc.Instance["Problem_HotkeyInUse"]);
    }

    [Fact]
    public async Task Record_HeldByAProfile_NamesIt_ButTheProfileItselfMayKeepIt()
    {
        Profile rig = Rig() with { Hotkey = CtrlAltR };
        await _host.Catalog.SaveAsync(rig, Ct);

        var other = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Toggle, Guid.Empty, "Settings_ToggleHotkeyHint");
        other.Record(CtrlAltR.Modifiers, CtrlAltR.VirtualKey).ShouldBeNull();
        other.Hint.ShouldBe(HotkeyService.UsedByText(new HotkeyUse(HotkeyUseKind.Profile, rig.Id, rig.Name)));
        other.Reset();
        other.Hint.ShouldBe(Loc.Instance["Settings_ToggleHotkeyHint"]);

        var own = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Profile, rig.Id);
        own.Record(CtrlAltR.Modifiers, CtrlAltR.VirtualKey).ShouldBe(CtrlAltR);
        own.Hint.ShouldBe(Loc.Instance["Editor_HotkeyHint"]);
    }

    [Fact]
    public void Record_FreeCombination_IsTaken_AndTheProbeLetsGoOfIt()
    {
        var recorder = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Profile, Guid.NewGuid());

        recorder.Record(CtrlAltD.Modifiers, CtrlAltD.VirtualKey).ShouldBe(CtrlAltD);

        _registrar.Held.ShouldBeEmpty();
    }

    [Fact]
    public async Task BeginAndEnd_ReleaseAndTakeBackRigShiftsHotkeys()
    {
        await _host.Catalog.SaveAsync(Rig() with { Hotkey = CtrlAltR }, Ct);
        _hotkeys.Start();
        _registrar.Held.Values.ShouldContain(CtrlAltR);
        var recorder = new HotkeyRecorder(_hotkeys, HotkeyUseKind.Profile, Guid.NewGuid());

        recorder.Begin();
        _registrar.Held.ShouldBeEmpty("pressing the combination must record it, not switch");

        recorder.End();
        _registrar.Held.Values.ShouldContain(CtrlAltR);
    }

    [Fact]
    public void HotkeyBox_FieldGoneAndBack_RecordsOncePerFocus_AndEndsWhereItBegan() => RunSta(() =>
    {
        var box = new TextBox();
        var first = new FieldSpy();
        var second = new FieldSpy();

        HotkeyBox.SetField(box, first);
        HotkeyBox.SetField(box, null);
        HotkeyBox.SetField(box, first);
        Focus(box, Keyboard.GotKeyboardFocusEvent);
        first.Begun.ShouldBe(1, "handlers must not pile up when the field comes back");

        // Another entry is selected while the field is being recorded: the recording ends on the one that began.
        HotkeyBox.SetField(box, second);
        Focus(box, Keyboard.LostKeyboardFocusEvent);
        first.Ended.ShouldBe(1);
        second.Ended.ShouldBe(0);
    });

    public void Dispose()
    {
        _hotkeys.Dispose();
        _sessions.Dispose();
        _host.Dispose();
    }

    private static void Focus(UIElement element, RoutedEvent routedEvent) =>
        element.RaiseEvent(new KeyboardFocusChangedEventArgs(Keyboard.PrimaryDevice, 0, null, null) { RoutedEvent = routedEvent });

    private static void RunSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    private sealed class FieldSpy : IHotkeyField
    {
        public int Begun { get; private set; }

        public int Ended { get; private set; }

        public void BeginHotkeyRecording() => Begun++;

        public void EndHotkeyRecording() => Ended++;

        public void RecordHotkey(HotkeyModifiers modifiers, int virtualKey)
        {
        }

        public void ClearHotkey()
        {
        }
    }
}
