using RigShift.App.Localization;
using RigShift.App.ViewModels;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The app list both editors share: picking, reordering, and the USB device the programs wait for.</summary>
public sealed class AppListEditorTests
{
    private const string Wheelbase = "VID_0EB7&PID_0006";
    private const string Pedals = "VID_1111&PID_2222";

    private readonly FakeAppPicker _picker = new();

    [Fact]
    public void AddPicked_TakesThePickedProgram_AndCancellingAddsNothing()
    {
        using AppListEditor list = List([]);
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.AddPickedCommand.Execute(null);
        list.Items.ShouldBeEmpty();
        changes.ShouldBe(0);

        _picker.Answers.Enqueue(new PickedApp(@"C:\Tools\SimHub.exe", "SimHub"));
        list.AddPickedCommand.Execute(null);

        _picker.OpenedWith.ShouldBe([null, null], "a new entry starts without a path");
        list.Items.ShouldHaveSingleItem().DisplayName.ShouldBe("SimHub");
        list.Build().ShouldBe([new AppAction { Path = @"C:\Tools\SimHub.exe", Name = "SimHub" }]);
        changes.ShouldBe(1);
    }

    [Fact]
    public void Browse_StartsFromTheEntrysProgram_AndCancellingKeepsIt()
    {
        using AppListEditor list = List([new AppAction { Path = @"C:\Tools\old.exe" }]);
        AppEditItem entry = list.Items[0];

        list.BrowseCommand.Execute(entry);
        entry.Path.ShouldBe(@"C:\Tools\old.exe");

        _picker.Answers.Enqueue(new PickedApp(@"C:\Tools\CrewChief.exe", "Crew Chief"));
        list.BrowseCommand.Execute(entry);

        _picker.OpenedWith.ShouldBe([@"C:\Tools\old.exe", @"C:\Tools\old.exe"]);
        entry.Path.ShouldBe(@"C:\Tools\CrewChief.exe");
        entry.DisplayName.ShouldBe("Crew Chief");
    }

    [Fact]
    public void MoveAndRemove_ChangeTheOrderThatIsBuilt_AndTheEndsDoNotMove()
    {
        using AppListEditor list = List([App("a"), App("b"), App("c")]);
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.MoveUpCommand.Execute(list.Items[0]);
        list.MoveDownCommand.Execute(list.Items[2]);
        changes.ShouldBe(0, "the first cannot go up, the last cannot go down");

        list.MoveDownCommand.Execute(list.Items[0]);
        list.RemoveCommand.Execute(list.Items[2]);

        list.Build().Select(a => a.Path).ShouldBe([@"C:\b.exe", @"C:\a.exe"]);
        changes.ShouldBe(2);
    }

    [Fact]
    public void EntryChange_IsReported_AlsoForAnEntryAddedLater()
    {
        using AppListEditor list = List([App("a")]);
        list.Add(@"C:\b.exe");
        int changes = 0;
        list.Changed += (_, _) => changes++;

        list.Items[0].Arguments = "--fast";
        list.Items[1].WaitSeconds = 5;

        changes.ShouldBe(2);
    }

    [Fact]
    public void ShowWhen_ReachesEveryEntry()
    {
        using AppListEditor profile = List([App("a")]);
        using AppListEditor game = List([App("a")], showWhen: true);
        game.Add(@"C:\b.exe");

        profile.Items.ShouldAllBe(i => !i.ShowWhen);
        game.Items.ShouldAllBe(i => i.ShowWhen);
    }

    [Fact]
    public void WaitDevice_SavedButNotConnected_IsOfferedWithItsName_AndOthersFollow()
    {
        var device = new AppsWaitDeviceChoice(
            Pedals.ToLowerInvariant(), "Pedals", [new UsbDevice(Wheelbase, "Fanatec Wheelbase")], [new RuleDevice { Id = Pedals }], null);

        device.Choices[0].ShouldBe(new Choice(null, Loc.Instance["Editor_AppsWaitNone"]));
        device.Choices.Select(c => c.Key).ShouldBe([null, Wheelbase, Pedals], "connected first, the saved one once");
        device.DeviceId.ShouldBe(Pedals);
        device.DeviceName.ShouldBe("Pedals");

        device.Selected = device.Choices.First(c => c.Key == Wheelbase);
        device.DeviceName.ShouldBe("Fanatec Wheelbase");

        device.Selected = device.Choices[0];
        device.DeviceId.ShouldBeNull();
        device.DeviceName.ShouldBeNull();
    }

    [Fact]
    public void Relabel_KeepsEntriesAndDevice_AndChoosingAnotherDeviceIsReported()
    {
        var device = new AppsWaitDeviceChoice(Wheelbase, null, [new UsbDevice(Wheelbase, "Fanatec Wheelbase")], [], null);
        using AppListEditor list = List([App("a"), new AppAction { Path = "obs64", Kind = AppActionKind.Stop }], device: device);
        IReadOnlyList<AppAction> before = list.Build();

        list.Relabel();

        list.Build().ShouldBe(before);
        device.DeviceId.ShouldBe(Wheelbase);

        int changes = 0;
        list.Changed += (_, _) => changes++;
        device.Selected = device.Choices[0];
        changes.ShouldBe(1);
    }

    private static AppAction App(string name) => new() { Path = $@"C:\{name}.exe" };

    private AppListEditor List(IEnumerable<AppAction> apps, bool showWhen = false, AppsWaitDeviceChoice? device = null) =>
        new(apps, showWhen, device ?? new AppsWaitDeviceChoice(null, null, [], [], null), _picker, "App_Path");
}
