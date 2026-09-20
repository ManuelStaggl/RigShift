using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>The hotkey bookkeeping against a registrar that takes no real keys.</summary>
public sealed class HotkeyServiceTests : IDisposable
{
    private static readonly Hotkey CtrlAltR = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x52 };
    private static readonly Hotkey CtrlAltD = new() { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x44 };

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly InMemoryGameStore _games = new();
    private readonly GameCatalog _catalog;
    private readonly GameSessionService _sessions;
    private readonly FakeRegistrar _registrar = new();
    private readonly HotkeyService _service;
    private readonly List<IReadOnlyList<string>> _failures = [];

    public HotkeyServiceTests()
    {
        _catalog = new GameCatalog(_games, _host.Catalog, Logger.None);
        _sessions = new GameSessionService(
            _catalog,
            _host.Catalog,
            Substitute.For<IGameProcesses>(),
            () => throw new InvalidOperationException("no session is started in these tests"),
            TimeProvider.System,
            Logger.None);
        _service = new HotkeyService(_host.Catalog, _catalog, _sessions, _host.Coordinator, _host.Settings, Logger.None, _registrar);
        _service.RegistrationFailed += (_, names) => _failures.Add(names);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Start_RegistersProfilesAndGames()
    {
        await SaveProfileAsync("Rig", CtrlAltR);
        await SaveGameAsync("iRacing", CtrlAltD);

        _service.Start();

        _registrar.Held.Values.ShouldBe([CtrlAltR, CtrlAltD], ignoreOrder: true);
        _failures.ShouldBeEmpty();
    }

    /// <summary>A game with a profile's combination used to fail as "taken by another application" – and only at startup was anyone told.</summary>
    [Fact]
    public async Task GameSavedWithAProfilesHotkey_KeepsTheProfileAndReportsTheGame()
    {
        await SaveProfileAsync("Rig", CtrlAltR);
        _service.Start();

        await SaveGameAsync("iRacing", CtrlAltR);

        _registrar.Held.Values.ShouldBe([CtrlAltR]);
        _failures.ShouldHaveSingleItem().ShouldBe(["iRacing"]);
    }

    [Fact]
    public async Task HotkeyTakenByAnotherApp_AfterStartup_IsStillReported_ButOnlyOnce()
    {
        _service.Start();
        _registrar.TakenElsewhere.Add(CtrlAltD);

        await SaveProfileAsync("Desk", CtrlAltD);
        await SaveProfileAsync("Rig", CtrlAltR);
        _service.Suspend();
        _service.Resume();

        _failures.ShouldHaveSingleItem().ShouldBe(["Desk"]);
        _registrar.Held.Values.ShouldBe([CtrlAltR]);
    }

    [Fact]
    public async Task UsedBy_SeesProfilesGamesAndTheToggle_ButNotTheOneBeingEdited()
    {
        Profile rig = await SaveProfileAsync("Rig", CtrlAltR);
        GameEntry game = await SaveGameAsync("iRacing", CtrlAltD);

        _service.UsedBy(CtrlAltR, HotkeyUseKind.Game, game.Id).ShouldNotBeNull().Name.ShouldBe("Rig");
        _service.UsedBy(CtrlAltD, HotkeyUseKind.Toggle, Guid.Empty).ShouldNotBeNull().Name.ShouldBe("iRacing");
        _service.UsedBy(CtrlAltR, HotkeyUseKind.Profile, rig.Id).ShouldBeNull();
    }

    public void Dispose()
    {
        _service.Dispose();
        _sessions.Dispose();
        _host.Dispose();
    }

    private async Task<Profile> SaveProfileAsync(string name, Hotkey hotkey)
    {
        var profile = new Profile { Id = Guid.NewGuid(), Name = name, Displays = [], Hotkey = hotkey };
        await _host.Catalog.SaveAsync(profile, Ct);
        return profile;
    }

    private async Task<GameEntry> SaveGameAsync(string name, Hotkey hotkey)
    {
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = name,
            Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
            Hotkey = hotkey,
        };
        await _catalog.SaveAsync(game, Ct);
        return game;
    }

    private sealed class FakeRegistrar : IHotkeyRegistrar
    {
        public Dictionary<int, Hotkey> Held { get; } = [];

        /// <summary>Combinations another application holds: registering them fails with 1409.</summary>
        public HashSet<Hotkey> TakenElsewhere { get; } = [];

#pragma warning disable CS0067 // Nothing presses a key in these tests.
        public event EventHandler<int>? Pressed;
#pragma warning restore CS0067

        public bool Register(int id, Hotkey hotkey, out int error)
        {
            if (TakenElsewhere.Contains(hotkey) || Held.ContainsValue(hotkey))
            {
                error = 1409;
                return false;
            }

            error = 0;
            Held[id] = hotkey;
            return true;
        }

        public void Unregister(int id) => Held.Remove(id);

        public void Dispose()
        {
        }
    }
}
