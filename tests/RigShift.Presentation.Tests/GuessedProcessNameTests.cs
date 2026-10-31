using RigShift.App.Services;
using RigShift.Core.Games;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class GuessedProcessNameTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("Rally-Win64-Shipping")]
    [InlineData("Rally")]
    public async Task ALearnedName_ReplacesTheGuess_EvenWhenItIsTheSame(string learned)
    {
        var games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "Rally",
            Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "1", ProcessName = "Rally", ProcessNameGuessed = true },
        };
        await games.SaveAsync(game, Ct);

        await games.RememberProcessNameAsync(game.Id, learned, Ct);

        GameLaunch launch = games.Find(game.Id).ShouldNotBeNull().Launch;
        launch.ProcessName.ShouldBe(learned);
        launch.ProcessNameGuessed.ShouldBeFalse();
    }

    public void Dispose() => _host.Dispose();
}
