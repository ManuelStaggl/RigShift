using RigShift.App.Services;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>Saving under a new name keeps the old one findable, so keys, links and shortcuts with it keep working.</summary>
public sealed class RenameTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RenamedProfile_IsStillFoundByItsOldName()
    {
        Profile rig = Rig();
        await _host.Store.SaveAsync(rig, Ct);
        await _host.Catalog.ReloadAsync(Ct);

        // The editor builds the profile anew, without the former names.
        await _host.Catalog.SaveAsync(rig with { Name = "Cockpit" }, Ct);
        await _host.Catalog.ReloadAsync(Ct);

        ProfileEditing.FindByName(_host.Catalog.Profiles, "rig").ShouldNotBeNull().Name.ShouldBe("Cockpit");
    }

    [Fact]
    public async Task RenamedGame_IsStillFoundByItsOldName()
    {
        var store = new InMemoryGameStore();
        var games = new GameCatalog(store, _host.Catalog, Logger.None);
        var game = new GameEntry { Id = Guid.NewGuid(), Name = "iRacing", Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" } };
        await games.SaveAsync(game, Ct);

        await games.SaveAsync(game with { Name = "iRacing VR" }, Ct);

        GameEditing.FindByName(games.Games, "IRACING").ShouldNotBeNull().Name.ShouldBe("iRacing VR");
    }

    public void Dispose() => _host.Dispose();
}
