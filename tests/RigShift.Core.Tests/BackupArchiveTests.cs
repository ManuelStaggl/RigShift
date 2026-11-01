using System.IO.Compression;
using RigShift.Core.Abstractions;
using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class BackupArchiveTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Profiles => Path.Combine(_directory, "profiles");

    private string SettingsFile => Path.Combine(_directory, "settings.json");

    [Fact]
    public async Task WriteThenRestore_RoundTripsProfilesAndSettings()
    {
        var store = new JsonProfileStore(Profiles, Logger.None);
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig() with { Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x70 } };
        await store.SaveAsync(desk, Ct);
        await store.SaveAsync(rig, Ct);
        var settingsStore = new JsonSettingsStore(SettingsFile, Logger.None);
        await settingsStore.SaveAsync(new AppSettings { DefaultProfileId = desk.Id, Language = "de", ConfirmTimeoutSeconds = 7 }, Ct);
        using var archive = new MemoryStream();

        BackupArchive.Write(_directory, archive).ShouldBe(2);

        // A different data folder, with a profile that is not in the backup: it must be gone afterwards.
        string other = _directory + "-restored";
        await new JsonProfileStore(Path.Combine(other, "profiles"), Logger.None).SaveAsync(Profile("Old", DeskModes), Ct);
        archive.Position = 0;
        BackupContent content = BackupArchive.Inspect(archive);
        BackupArchive.Restore(other, content, Logger.None);

        IReadOnlyList<Profile> restored = (await new JsonProfileStore(Path.Combine(other, "profiles"), Logger.None).LoadAllAsync(Ct)).Profiles;
        restored.Select(p => p.Name).ShouldBe(["Desk", "Rig"]);
        Profile restoredRig = restored.Single(p => p.Name == "Rig");
        restoredRig.Id.ShouldBe(rig.Id);
        restoredRig.Hotkey.ShouldBe(rig.Hotkey);
        restoredRig.Displays.Count.ShouldBe(rig.Displays.Count);
        AppSettings settings = await new JsonSettingsStore(Path.Combine(other, "settings.json"), Logger.None).LoadAsync(Ct);
        settings.DefaultProfileId.ShouldBe(desk.Id);
        settings.Language.ShouldBe("de");
        settings.ConfirmTimeoutSeconds.ShouldBe(7);
        Directory.Delete(other, recursive: true);
    }

    [Fact]
    public async Task Write_WithoutSettingsFile_ArchivesOnlyProfiles_RestoreKeepsSettings()
    {
        await new JsonProfileStore(Profiles, Logger.None).SaveAsync(Rig(), Ct);
        using var archive = new MemoryStream();
        BackupArchive.Write(_directory, archive).ShouldBe(1);
        await new JsonSettingsStore(SettingsFile, Logger.None).SaveAsync(new AppSettings { Language = "en" }, Ct);

        archive.Position = 0;
        BackupContent content = BackupArchive.Inspect(archive);
        BackupArchive.Restore(_directory, content, Logger.None);

        content.Settings.ShouldBeNull();
        (await new JsonSettingsStore(SettingsFile, Logger.None).LoadAsync(Ct)).Language.ShouldBe("en");
    }

    [Fact]
    public async Task WriteThenRestore_RoundTripsTheGames()
    {
        await new JsonProfileStore(Profiles, Logger.None).SaveAsync(Rig(), Ct);
        GameEntry game = Game("iRacing");
        await new JsonGameStore(_directory, Logger.None).SaveAsync(game, Ct);
        using var archive = new MemoryStream();
        BackupArchive.Write(_directory, archive);

        string other = _directory + "-restored";
        await new JsonGameStore(other, Logger.None).SaveAsync(Game("Old game"), Ct);
        archive.Position = 0;
        BackupContent content = BackupArchive.Inspect(archive);
        BackupArchive.Restore(other, content, Logger.None);

        content.Games.ShouldNotBeNull().ShouldHaveSingleItem().Name.ShouldBe("iRacing");
        GameLoadResult loaded = await new JsonGameStore(other, Logger.None).LoadAllAsync(Ct);
        loaded.Games.ShouldHaveSingleItem().Id.ShouldBe(game.Id);
        Directory.Delete(other, recursive: true);
    }

    /// <summary>A backup from before 2.x knows no games: restoring it must not wipe them.</summary>
    [Fact]
    public async Task Restore_BackupWithoutGames_LeavesTheGamesAlone()
    {
        await new JsonGameStore(_directory, Logger.None).SaveAsync(Game("iRacing"), Ct);
        using var archive = Zip(("profiles/a.json", ProfileJson(Rig())));

        BackupContent content = BackupArchive.Inspect(archive);
        BackupArchive.Restore(_directory, content, Logger.None);

        content.Games.ShouldBeNull();
        (await new JsonGameStore(_directory, Logger.None).LoadAllAsync(Ct)).Games.ShouldHaveSingleItem().Name.ShouldBe("iRacing");
    }

    /// <summary>
    /// The write fails halfway – here a file that cannot be replaced. The profiles that were there must still be
    /// there: nothing is removed before everything new is in place.
    /// </summary>
    [Fact]
    public async Task Restore_FailingHalfway_KeepsTheOldProfiles()
    {
        var store = new JsonProfileStore(Profiles, Logger.None);
        Profile old = Profile("Old", DeskModes);
        Profile rig = Rig();
        await store.SaveAsync(old, Ct);
        await store.SaveAsync(rig, Ct);
        using var archive = Zip(("profiles/a.json", ProfileJson(rig)));
        BackupContent content = BackupArchive.Inspect(archive);

        string target = Path.Combine(Profiles, rig.Id.ToString("D") + ".json");
        using (new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            // Windows reports the locked target as a sharing violation or as "access denied", depending on the lock.
            Exception failure = Should.Throw<Exception>(() => BackupArchive.Restore(_directory, content, Logger.None));
            (failure is IOException or UnauthorizedAccessException).ShouldBeTrue(failure.ToString());
        }

        (await store.LoadAllAsync(Ct)).Profiles.Select(p => p.Name).ShouldBe(["Old", rig.Name], ignoreOrder: true);
        Directory.Exists(Path.Combine(_directory, "profiles.restore")).ShouldBeFalse();
    }

    [Fact]
    public void Inspect_EntryThatUnpacksToMegabytes_IsRejected()
    {
        using var archive = Zip(("profiles/a.json", new string(' ', 2 * BoundedRead.DefaultLimit)));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"games":[{"id":"6f1b6f0e-0000-0000-0000-000000000001","name":"","launch":{"kind":"steam","target":"1"}}]}""")]
    [InlineData("""{"schemaVersion":1,"games":[{"id":"6f1b6f0e-0000-0000-0000-000000000001","name":"A","launch":null}]}""")]
    [InlineData("""{"schemaVersion":99,"games":[]}""")]
    [InlineData("not json")]
    public void Inspect_BrokenGames_IsRejected(string json)
    {
        using var archive = Zip(("games.json", json));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive)).Message.ShouldContain("games.json");
    }

    /// <summary>What a foreign backup would run has to be in front of the user before it is restored.</summary>
    [Fact]
    public void Review_ListsProgramsRulesAndHotkeys_AndMarksWhatNeedsASecondLook()
    {
        Profile rig = Rig() with
        {
            Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control, VirtualKey = 0x70 },
            Apps =
            [
                new AppAction { Kind = AppActionKind.Start, Path = @"C:\Tools\SimHub.exe", Arguments = "-min" },
                new AppAction { Kind = AppActionKind.Start, Path = @"\\evil\share\x.exe" },
                new AppAction { Kind = AppActionKind.Start, Path = "powershell", Arguments = "-c calc" },
                new AppAction { Kind = AppActionKind.Stop, Path = "discord" },
            ],
        };
        GameEntry game = Game("rFactor") with { Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"D:\rF\rf.exe" } };
        var settings = new AppSettings
        {
            AutomationRules = [new AutomationRule { ProfileId = rig.Id, SkipConfirmation = true, Devices = [new RuleDevice { Id = "USB\\X", Name = "Wheel" }] }],
        };

        IReadOnlyList<BackupItem> items = BackupReview.Review(new BackupContent([rig], settings, [game]));

        items.Select(i => i.Kind).ShouldBe([
            BackupItemKind.StartsProgram, BackupItemKind.StartsProgram, BackupItemKind.StartsProgram, BackupItemKind.StopsProgram,
            BackupItemKind.Hotkey, BackupItemKind.StartsGame, BackupItemKind.Rule,
        ]);
        items[0].Detail.ShouldBe(@"C:\Tools\SimHub.exe -min");
        items[0].NeedsAttention.ShouldBeFalse();
        items[1].IsNetworkPath.ShouldBeTrue();
        items[2].IsNotFullPath.ShouldBeTrue();
        items[3].NeedsAttention.ShouldBeFalse(); // Ending a program by its name starts nothing.
        items[6].SkipsConfirmation.ShouldBeTrue();
        items[6].Owner.ShouldBe(rig.Name);
        items[6].Detail.ShouldBe("Wheel");
    }

    [Fact]
    public void Restore_WritesProfilesUnderTheirOwnId_NotTheEntryName()
    {
        Profile rig = Rig();
        using var archive = Zip(("profiles/anything.json", ProfileJson(rig)));

        BackupArchive.Restore(_directory, BackupArchive.Inspect(archive), Logger.None);

        Directory.EnumerateFiles(Profiles).Select(Path.GetFileName).ShouldBe([rig.Id.ToString("D") + ".json"]);
    }

    [Theory]
    [InlineData("readme.txt", "hello")]
    [InlineData("profiles/sub/x.json", "{}")]
    [InlineData("../settings.json", "{}")]
    public void Inspect_UnknownEntry_IsRejected(string name, string content)
    {
        using var archive = Zip((name, content));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive)).Message.ShouldContain(name);
    }

    [Fact]
    public void Inspect_NotAZip_IsRejected()
    {
        using var stream = new MemoryStream([1, 2, 3, 4]);

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(stream)).Message.ShouldContain("ZIP");
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{ \"schemaVersion\": 1 }")]
    [InlineData("{ \"schemaVersion\": 99, \"profile\": { \"id\": \"11111111-1111-1111-1111-111111111111\", \"name\": \"X\", \"displays\": [] } }")]
    public void Inspect_BrokenProfile_IsRejected(string json)
    {
        using var archive = Zip(("profiles/a.json", json));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive));
    }

    [Fact]
    public void Inspect_EmptyArchive_IsRejected()
    {
        using var archive = Zip();

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive));
    }

    [Fact]
    public void Inspect_SameProfileTwice_IsRejected()
    {
        Profile rig = Rig();
        using var archive = Zip(("profiles/a.json", ProfileJson(rig)), ("profiles/b.json", ProfileJson(rig)));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive)).Message.ShouldContain("twice");
    }

    [Fact]
    public void Inspect_SettingsFromANewerVersion_IsRejected()
    {
        using var archive = Zip(("settings.json", "{ \"schemaVersion\": 99 }"));

        Should.Throw<InvalidDataException>(() => BackupArchive.Inspect(archive)).Message.ShouldContain("newer");
    }

    [Fact]
    public void Inspect_DoesNotTouchTheDisk()
    {
        using var archive = Zip(("profiles/a.json", ProfileJson(Rig())));

        BackupArchive.Inspect(archive).Profiles.ShouldHaveSingleItem();

        Directory.Exists(_directory).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static GameEntry Game(string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" },
    };

    private string ProfileJson(Profile profile)
    {
        // The store writes the exact file format; read it back as text. Outside _directory, which some tests expect untouched.
        string folder = _directory + "-scratch-" + Guid.NewGuid().ToString("N");
        new JsonProfileStore(folder, Logger.None).SaveAsync(profile, Ct).GetAwaiter().GetResult();
        string json = File.ReadAllText(Directory.EnumerateFiles(folder).Single());
        Directory.Delete(folder, recursive: true);
        return json;
    }

    private static MemoryStream Zip(params (string Name, string Content)[] entries)
    {
        var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string content) in entries)
            {
                using Stream entry = archive.CreateEntry(name).Open();
                using var writer = new StreamWriter(entry);
                writer.Write(content);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
