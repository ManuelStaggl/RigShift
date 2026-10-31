using System.ComponentModel;
using System.IO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Topology;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class GameEditorFactoryTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly GameCatalog _catalog;
    private readonly IGameLibrary _library = Substitute.For<IGameLibrary>();
    private readonly IWindowLayout _windows = Substitute.For<IWindowLayout>();
    private readonly IUsbDeviceList _usb = Substitute.For<IUsbDeviceList>();
    private readonly GameEditorFactory _factory;

    public GameEditorFactoryTests()
    {
        _catalog = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        var sessions = new GameSessionService(
            _catalog, _host.Catalog, Substitute.For<IGameProcesses>(), () => throw new InvalidOperationException("no session"), TimeProvider.System, Logger.None);
        var hotkeys = new HotkeyService(_host.Catalog, _catalog, sessions, _host.Coordinator, _host.Settings, Logger.None, new FakeHotkeyRegistrar());
        var services = new GameEditorServices(_catalog, hotkeys, new FakeAppPicker(), new FakeOpenXrRuntimes(), Logger.None);
        _factory = new GameEditorFactory(services, _host.Catalog, _library, _windows, _usb, _host.Settings);
    }

    public void Dispose() => _host.Dispose();

    private static InstalledGame Steam(string name, string id) => new(name, new GameLaunch { Kind = GameLaunchKind.Steam, Target = id });

    [Fact]
    public async Task AddInstalledAsync_NewGames_AreSavedWithoutTheEditor()
    {
        AddedGames result = await _factory.AddInstalledAsync([Steam("iRacing", "266410"), Steam("Le Mans Ultimate", "2399420")]);

        result.Added.Select(g => g.Name).ShouldBe(["iRacing", "Le Mans Ultimate"]);
        result.Skipped.ShouldBe(0);
        _catalog.Games.Count.ShouldBe(2);
    }

    [Fact]
    public async Task AddInstalledAsync_GameAlreadyConfigured_IsSkipped()
    {
        await _factory.AddInstalledAsync([Steam("iRacing", "266410")]);

        AddedGames again = await _factory.AddInstalledAsync([Steam("iRacing", "266410")]);

        again.Added.ShouldBeEmpty();
        again.Skipped.ShouldBe(1);
        _catalog.Games.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AddInstalledAsync_SameNameOtherLaunch_GetsUniqueName()
    {
        await _factory.AddInstalledAsync([Steam("Assetto Corsa", "244210")]);

        AddedGames second = await _factory.AddInstalledAsync([Steam("Assetto Corsa", "805550")]);

        second.Added.Single().Name.ShouldNotBe("Assetto Corsa");
    }

    [Fact]
    public async Task FindInstalledAsync_LibraryFails_ReturnsEmpty()
    {
        _library.Find().Throws(new IOException("disk gone"));

        (await _factory.FindInstalledAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task FindInstalledAsync_ReturnsTheLibrary()
    {
        _library.Find().Returns([Steam("iRacing", "266410")]);

        (await _factory.FindInstalledAsync()).Single().Name.ShouldBe("iRacing");
    }

    [Fact]
    public void OpenWindows_ListingFails_ReturnsEmpty()
    {
        _windows.Open().Throws(new Win32Exception(5));

        _factory.OpenWindows().ShouldBeEmpty();
    }

    [Fact]
    public async Task CreateEditorAsync_UsbListingFails_StillBuildsTheEditor()
    {
        _usb.ConnectedDevices().Throws(new Win32Exception(5));
        var game = new GameEntry { Id = Guid.NewGuid(), Name = "iRacing", Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" } };

        GameEditorViewModel editor = await _factory.CreateEditorAsync(game, isNew: true);

        editor.Name.ShouldBe("iRacing");
    }
}

public sealed class WindowCaptureViewModelTests
{
    private static OpenWindow Window(string process, string title, int left = 0) =>
        new(1, process, title, new PixelRect(left, 0, left + 800, 600), WindowState.Normal);

    [Fact]
    public void Constructor_PreviousLayout_TicksItsWindowsAgain()
    {
        var current = new WindowLayout { Windows = [new WindowPlacement { ProcessName = "SimHubWPF", Bounds = new PixelRect(0, 0, 10, 10) }] };

        var model = new WindowCaptureViewModel([Window("SimHubWPF", "SimHub"), Window("CrewChiefV4", "Crew Chief")], current);

        model.Windows.Single(w => w.IsChosen).Window.ProcessName.ShouldBe("SimHubWPF");
        model.CanSave.ShouldBeTrue();
    }

    [Fact]
    public void CanSave_NothingTicked_IsFalse()
    {
        var model = new WindowCaptureViewModel([Window("SimHubWPF", "SimHub")], current: null);

        model.CanSave.ShouldBeFalse();
    }

    [Fact]
    public void ToLayout_KeepsOnlyTickedWindows_AndDropsEmptyTitles()
    {
        var model = new WindowCaptureViewModel([Window("SimHubWPF", " ", left: 3840), Window("CrewChiefV4", "Crew Chief")], current: null);
        model.Windows[0].IsChosen = true;

        WindowLayout layout = model.ToLayout();

        WindowPlacement placed = layout.Windows.ShouldHaveSingleItem();
        placed.ProcessName.ShouldBe("SimHubWPF");
        placed.Title.ShouldBeNull();
        placed.Bounds.Left.ShouldBe(3840);
    }

    [Fact]
    public void CaptureRow_TextsNameProcessSizeAndPosition()
    {
        var row = new CaptureRow(Window("SimHubWPF", "", left: 3840));

        row.Title.ShouldBe("SimHubWPF");
        row.Details.ShouldContain("800");
        row.Position.ShouldStartWith("3840");
    }

    [Fact]
    public void ChosenText_FollowsTheTicks()
    {
        var model = new WindowCaptureViewModel([Window("SimHubWPF", "SimHub"), Window("CrewChiefV4", "Crew Chief")], current: null);
        string before = model.ChosenText;

        model.Windows[1].IsChosen = true;

        model.ChosenText.ShouldNotBe(before);
    }
}
