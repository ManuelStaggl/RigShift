using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class JsonGameStoreTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string File_ => Path.Combine(_directory, JsonGameStore.FileName);

    private static GameEntry Iracing(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "iRacing",
        Icon = "rig",
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", InstallFolder = @"D:\Steam\steamapps\common\iRacing" },
    };

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheWholeEntry()
    {
        var store = new JsonGameStore(_directory, Logger.None);
        Guid profileId = Guid.NewGuid();
        GameEntry game = Iracing() with
        {
            ProfileId = profileId,
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Start, Path = @"%ProgramFiles%\SimHub\SimHubWPF.exe", WaitSeconds = 2 } },
            AppsWaitForUsbDeviceId = "VID_16D0&PID_0D5A",
            AppsWaitForUsbDeviceName = "Simucube 2 Pro",
            Exit = new GameExitAction { Kind = GameExitKind.Profile, ProfileId = profileId, StopApps = true },
            Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x71 },
            StartWithGame = true,
        };

        await store.SaveAsync(game, Ct);
        GameLoadResult loaded = await store.LoadAllAsync(Ct);

        loaded.IsComplete.ShouldBeTrue();
        loaded.Games.Single().ShouldBeEquivalentTo(game);
    }

    /// <summary>The file is the only one, so a second entry must not replace the first.</summary>
    [Fact]
    public async Task Save_AddsEntries_AndReplacesOnlyTheSameId()
    {
        var store = new JsonGameStore(_directory, Logger.None);
        GameEntry iracing = Iracing();
        GameEntry ams2 = Iracing(Guid.NewGuid()) with { Name = "Automobilista 2" };

        await store.SaveAsync(iracing, Ct);
        await store.SaveAsync(ams2, Ct);
        await store.SaveAsync(iracing with { Name = "iRacing (VR)" }, Ct);

        IReadOnlyList<GameEntry> games = (await store.LoadAllAsync(Ct)).Games;
        games.Select(g => g.Name).ShouldBe(["Automobilista 2", "iRacing (VR)"]);
    }

    [Fact]
    public async Task Delete_RemovesOnlyThatEntry()
    {
        var store = new JsonGameStore(_directory, Logger.None);
        GameEntry iracing = Iracing();
        GameEntry ams2 = Iracing(Guid.NewGuid()) with { Name = "Automobilista 2" };
        await store.SaveAsync(iracing, Ct);
        await store.SaveAsync(ams2, Ct);

        await store.DeleteAsync(iracing.Id, Ct);

        (await store.LoadAllAsync(Ct)).Games.Single().Name.ShouldBe("Automobilista 2");
    }

    [Fact]
    public async Task Save_WritesVersionedReadableJson()
    {
        var store = new JsonGameStore(_directory, Logger.None);

        await store.SaveAsync(Iracing(), Ct);

        string json = await System.IO.File.ReadAllTextAsync(File_, Ct);
        json.ShouldContain("\"schemaVersion\": 1");
        json.ShouldContain("\"kind\": \"Steam\"");
        // The URI is computed from kind and target and must not end up in the file.
        json.ShouldNotContain("steam://");
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Load_ReportsABrokenFileInsteadOfPretendingThereAreNoGames()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File_, "{ not json", Ct);
        var store = new JsonGameStore(_directory, Logger.None);

        GameLoadResult loaded = await store.LoadAllAsync(Ct);

        loaded.Games.ShouldBeEmpty();
        loaded.IsComplete.ShouldBeFalse();
    }

    /// <summary>Overwriting a file we could not read would throw away every entry in it.</summary>
    [Fact]
    public async Task Save_RefusesWhenTheExistingFileIsUnreadable()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File_, "{ not json", Ct);
        var store = new JsonGameStore(_directory, Logger.None);

        await Should.ThrowAsync<InvalidOperationException>(() => store.SaveAsync(Iracing(), Ct));
    }

    [Fact]
    public async Task Load_SkipsAFileFromANewerVersion()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File_, "{\"schemaVersion\": 99, \"games\": []}", Ct);
        var store = new JsonGameStore(_directory, Logger.None);

        (await store.LoadAllAsync(Ct)).IsComplete.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_OfAMissingFileIsEmptyButComplete()
    {
        GameLoadResult loaded = await new JsonGameStore(_directory, Logger.None).LoadAllAsync(Ct);

        loaded.Games.ShouldBeEmpty();
        loaded.IsComplete.ShouldBeTrue();
    }

    /// <summary>
    /// "End the companion apps" must survive a file that does not name it: an <c>init</c> initializer would be
    /// skipped for a missing key, which is why the property has a setter.
    /// </summary>
    [Fact]
    public async Task Load_FileWithoutStopApps_KeepsEndingTheApps()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(
            File_,
            """
            {
              "schemaVersion": 1,
              "games": [
                {
                  "id": "6d5f7f2e-0d4a-4a1e-9f6a-6f0f2b6f0001",
                  "name": "iRacing",
                  "launch": { "kind": "Steam", "target": "266410" }
                }
              ]
            }
            """,
            Ct);
        var store = new JsonGameStore(_directory, Logger.None);

        GameEntry game = (await store.LoadAllAsync(Ct)).Games.Single();

        game.Exit.Kind.ShouldBe(GameExitKind.Stay);
        game.Exit.StopApps.ShouldBeTrue();
        game.Apps.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(GameLaunchKind.Steam, "266410", "steam://rungameid/266410")]
    [InlineData(GameLaunchKind.Epic, "Snapdragon", "com.epicgames.launcher://apps/Snapdragon?action=launch&silent=true")]
    public void Uri_IsBuiltFromKindAndTarget(GameLaunchKind kind, string target, string expected)
        => new GameLaunch { Kind = kind, Target = target }.Uri.ShouldBe(expected);

    [Fact]
    public void KnownProcessName_ComesFromThePathForAnExecutable_AndFromTheLearnedNameOtherwise()
    {
        new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\Games\rF2\rFactor2.exe" }
            .KnownProcessName().ShouldBe("rFactor2");
        new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" }
            .KnownProcessName().ShouldBeNull();
        new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", ProcessName = "iRacingSim64DX11" }
            .KnownProcessName().ShouldBe("iRacingSim64DX11");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
