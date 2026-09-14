using System.Text.Json.Nodes;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class JsonProfileStoreTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SaveThenLoad_RoundTripsTheWholeProfile()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig(confirmSeconds: 15, audio: new AudioAssignment
        {
            Playback = new AudioEndpoint("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}", "Headphones"),
            PlaybackVolumePercent = 40,
            RecordingVolumePercent = 75,
        }) with
        {
            Icon = "rig",
            Hotkey = new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 },
            // A List, like the deserializer creates: Shouldly compares the collection type too.
            Apps = new List<AppAction> { new() { Kind = AppActionKind.Stop, Path = "%ProgramFiles%\\SimHub\\SimHubWPF.exe", Arguments = "-x", WaitSeconds = 2 } },
        };

        await store.SaveAsync(rig, Ct);
        IReadOnlyList<Profile> loaded = await store.LoadAllAsync(Ct);

        Profile result = loaded.Single();
        result.ShouldBeEquivalentTo(rig);
        result.Displays[1].IsOptional.ShouldBeTrue();
    }

    [Fact]
    public async Task Save_WritesVersionedReadableJson()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig();

        await store.SaveAsync(rig, Ct);

        string json = await File.ReadAllTextAsync(Path.Combine(_directory, rig.Id.ToString("D") + ".json"), Ct);
        json.ShouldContain("\"schemaVersion\": 1");
        json.ShouldContain("\"rotation\": \"Identity\"");
        Directory.GetFiles(_directory, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public async Task Save_OverwritesExistingProfile()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig();

        await store.SaveAsync(rig, Ct);
        await store.SaveAsync(rig with { Name = "Rig renamed" }, Ct);

        (await store.LoadAllAsync(Ct)).Single().Name.ShouldBe("Rig renamed");
    }

    [Fact]
    public async Task Load_SkipsBrokenAndNewerFiles_ButReturnsTheRest()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        await store.SaveAsync(Rig(), Ct);
        await File.WriteAllTextAsync(Path.Combine(_directory, "broken.json"), "{ not json", Ct);
        await File.WriteAllTextAsync(Path.Combine(_directory, "future.json"), "{\"schemaVersion\": 99, \"profile\": null}", Ct);

        IReadOnlyList<Profile> loaded = await store.LoadAllAsync(Ct);

        loaded.Single().Name.ShouldBe("Rig");
    }

    [Fact]
    public async Task Load_ProfileFromVersion1_2_HasNoApps()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig();
        await store.SaveAsync(rig, Ct);
        string file = Path.Combine(_directory, rig.Id.ToString("D") + ".json");
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        document["profile"]!.AsObject().Remove("apps").ShouldBeTrue();
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);

        Profile loaded = (await store.LoadAllAsync(Ct)).Single();

        loaded.Apps.ShouldNotBeNull();
        loaded.Apps.ShouldBeEmpty();
    }

    [Fact]
    public async Task Load_HandWrittenProfileWithoutOptionalKeys_UsesDefaults()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig();
        await store.SaveAsync(rig, Ct);
        string file = Path.Combine(_directory, rig.Id.ToString("D") + ".json");
        JsonObject profile = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!["profile"]!.AsObject();
        JsonObject display = profile["displays"]![0]!.AsObject();
        display.Remove("rotation").ShouldBeTrue();
        display["identity"]!.AsObject().Remove("friendlyName").ShouldBeTrue();
        profile.Remove("audio").ShouldBeTrue();
        profile.Remove("appsWaitSeconds").ShouldBeTrue();
        await File.WriteAllTextAsync(file, profile.Parent!.ToJsonString(), Ct);

        Profile loaded = (await store.LoadAllAsync(Ct)).Single();

        loaded.Displays[0].Rotation.ShouldBe(DisplayRotation.Identity);
        loaded.Displays[0].Identity.FriendlyName.ShouldBe(string.Empty);
        loaded.Audio.ShouldNotBeNull();
        loaded.AppsWaitSeconds.ShouldBe(Profiles.Profile.DefaultAppsWaitSeconds);
        loaded.DisableCommunicationsDucking.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_AudioEndpointWithoutName_HasEmptyName()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig(audio: new AudioAssignment { Playback = new AudioEndpoint("{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}", "Headphones") });
        await store.SaveAsync(rig, Ct);
        string file = Path.Combine(_directory, rig.Id.ToString("D") + ".json");
        JsonNode document = JsonNode.Parse(await File.ReadAllTextAsync(file, Ct))!;
        document["profile"]!["audio"]!["playback"]!.AsObject().Remove("friendlyName").ShouldBeTrue();
        await File.WriteAllTextAsync(file, document.ToJsonString(), Ct);

        Profile loaded = (await store.LoadAllAsync(Ct)).Single();

        loaded.Audio.Playback!.FriendlyName.ShouldBe(string.Empty);
    }

    [Fact]
    public async Task Load_MissingDirectory_ReturnsEmpty()
    {
        var store = new JsonProfileStore(_directory, Logger.None);

        (await store.LoadAllAsync(Ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Delete_RemovesProfile_AndIgnoresUnknownIds()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig();
        await store.SaveAsync(rig, Ct);

        await store.DeleteAsync(rig.Id, Ct);
        await store.DeleteAsync(Guid.NewGuid(), Ct);

        (await store.LoadAllAsync(Ct)).ShouldBeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
