using System.Text.Json.Nodes;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

/// <summary>
/// Up to 4.x an app had seconds and "until its window", and the box turned the seconds into a limit (none meaning a
/// minute). Each old combination has to come back as the one wait choice that does the same.
/// </summary>
public sealed class AppWaitMigrationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <param name="waitForWindow">null: the key is missing, as in files from before the window wait existed.</param>
    [Theory]
    [InlineData("Start", 0, false, AppWait.None, 0)]
    [InlineData("Start", 5, false, AppWait.Fixed, 5)]
    [InlineData("Start", 0, true, AppWait.Window, 60)]
    [InlineData("Start", 20, true, AppWait.Window, 20)]
    [InlineData("Start", 3, null, AppWait.Fixed, 3)]
    [InlineData("Start", 0, null, AppWait.None, 0)]
    [InlineData("Stop", 0, false, AppWait.None, 0)]
    [InlineData("Stop", 5, false, AppWait.Fixed, 5)]
    [InlineData("Stop", 0, true, AppWait.None, 0)]
    [InlineData("Stop", 5, true, AppWait.Fixed, 5)]
    public async Task Load_4xProfile_KeepsWhatTheAppWaited(string kind, int seconds, bool? waitForWindow, AppWait wait, int waitSeconds)
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        string file = await WriteProfileAsync(store, OldApp(kind, seconds, waitForWindow));

        AppAction app = (await store.LoadAllAsync(Ct)).Profiles.Single().Apps.Single();

        app.Wait.ShouldBe(wait);
        app.WaitSeconds.ShouldBe(waitSeconds);
        (await ReloadedAfterSaveAsync(store, file)).ShouldSatisfyAllConditions(
            again => again.app.Wait.ShouldBe(wait),
            again => again.app.WaitSeconds.ShouldBe(waitSeconds),
            again => again.stored.ContainsKey("waitForWindow").ShouldBeFalse(),
            again => again.stored["wait"]!.GetValue<string>().ShouldBe(wait.ToString()));
    }

    /// <summary>Games carry the same app entries in their own file.</summary>
    [Fact]
    public async Task Load_4xGame_KeepsTheWindowWaitOfAMinute()
    {
        var store = new JsonGameStore(_directory, Logger.None);
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "iRacing",
            Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\iRacing\iRacingUI.exe" },
            Apps = [new AppAction { Path = "SimHub.exe" }],
        };
        await store.SaveAsync(game, Ct);
        string file = Path.Combine(_directory, JsonGameStore.FileName);
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        document["games"]![0]!["apps"] = new JsonArray(OldApp("Start", 0, true));
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);

        AppAction app = (await store.LoadAllAsync(Ct)).Games.Single().Apps.Single();

        app.Wait.ShouldBe(AppWait.Window);
        app.WaitSeconds.ShouldBe(AppAction.DefaultWindowWaitSeconds);
    }

    /// <summary>An editor compares the stored form; a loaded old entry must not look changed against its new form.</summary>
    [Fact]
    public async Task Load_4xProfile_IsTheSameAsTheEntryWrittenNew()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        await WriteProfileAsync(store, OldApp("Start", 0, true));

        Profile loaded = (await store.LoadAllAsync(Ct)).Profiles.Single();
        Profile written = loaded with
        {
            Apps = [new AppAction { Path = loaded.Apps[0].Path, Name = "SimHub", Wait = AppWait.Window, WaitSeconds = 60 }],
        };

        StoredForm.Same(loaded, written).ShouldBeTrue();
    }

    /// <summary>What 4.x wrote for an app: every key, "waitForWindow" included.</summary>
    private static JsonObject OldApp(string kind, int seconds, bool? waitForWindow)
    {
        var app = new JsonObject
        {
            ["kind"] = kind,
            ["path"] = @"C:\SimHub\SimHubWPF.exe",
            ["name"] = "SimHub",
            ["arguments"] = null,
            ["waitSeconds"] = seconds,
            ["when"] = "BeforeGame",
        };
        if (waitForWindow is { } value)
        {
            app["waitForWindow"] = value;
        }

        return app;
    }

    private async Task<string> WriteProfileAsync(JsonProfileStore store, JsonObject app)
    {
        Profile rig = Rig() with { Apps = [new AppAction { Path = "placeholder.exe" }] };
        await store.SaveAsync(rig, Ct);
        string file = Path.Combine(_directory, rig.Id.ToString("D") + ".json");
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        document["profile"]!["apps"] = new JsonArray(app);
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);
        return file;
    }

    private static async Task<(AppAction app, JsonObject stored)> ReloadedAfterSaveAsync(JsonProfileStore store, string file)
    {
        await store.SaveAsync((await store.LoadAllAsync(Ct)).Profiles.Single(), Ct);
        JsonObject stored = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["profile"]!["apps"]![0]!.AsObject();
        return ((await store.LoadAllAsync(Ct)).Profiles.Single().Apps.Single(), stored);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
