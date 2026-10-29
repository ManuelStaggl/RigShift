using System.IO;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class GameCatalogTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    private static readonly GameEntry Lmu = new()
    {
        Id = Guid.NewGuid(),
        Name = "Le Mans Ultimate (VR)",
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "2399420" },
        Settings = new GameSettings
        {
            File = @"C:\Games\LMU\UserData\player\Settings.JSON",
            Keys = ["Graphic Options/Shadows"],
            Values = new Dictionary<string, string> { ["Graphic Options/Shadows"] = "2" },
        },
    };

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task RememberSettings_SavesTheValuesAsTheEntrysOwn()
    {
        var store = new InMemoryGameStore();
        store.Games.Add(Lmu);
        var catalog = new GameCatalog(store, _host.Catalog, Logger.None);
        await catalog.ReloadAsync(Ct);

        await catalog.RememberSettingsAsync(Lmu.Id, new Dictionary<string, string> { ["Graphic Options/Shadows"] = "1" }, Ct);

        store.Games.Single().Settings!.Values["Graphic Options/Shadows"].ShouldBe("1");
        catalog.Find(Lmu.Id)!.Settings!.Values["Graphic Options/Shadows"].ShouldBe("1");
    }

    [Fact]
    public async Task RememberSettings_LeavesTheCatalogAsItWas_WhenSavingFails()
    {
        IGameStore store = Substitute.For<IGameStore>();
        store.LoadAllAsync(Arg.Any<CancellationToken>()).Returns(new GameLoadResult([Lmu], null));
        store.SaveAsync(Arg.Any<GameEntry>(), Arg.Any<CancellationToken>()).ThrowsAsync(new IOException("disk full"));
        var catalog = new GameCatalog(store, _host.Catalog, Logger.None);
        await catalog.ReloadAsync(Ct);

        await catalog.RememberSettingsAsync(Lmu.Id, new Dictionary<string, string> { ["Graphic Options/Shadows"] = "1" }, Ct);

        catalog.Find(Lmu.Id)!.Settings!.Values["Graphic Options/Shadows"].ShouldBe("2");
    }
}
