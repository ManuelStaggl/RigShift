using RigShift.Core.Settings;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>
/// A settings file that cannot be read used to become defaults without a word, and the next save wrote those over it:
/// USB rules, names and hotkeys gone, automatic updates back on.
/// </summary>
public sealed class JsonSettingsStoreDamageTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));
    private readonly FakeTime _time = new();

    private string File => Path.Combine(_directory, "settings.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private JsonSettingsStore Store() => new(File, Logger.None, _time);

    private async Task WriteAsync(string json)
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, json, Ct);
    }

    [Fact]
    public async Task Load_FileThatIsNotJson_KeepsACopyBeforeAnythingOverwritesIt()
    {
        const string damaged = """{ "onlyNotifyAboutUpdates": true, "automationRules": [ { "profil""";
        await WriteAsync(damaged);
        JsonSettingsStore store = Store();

        AppSettings settings = await store.LoadAsync(Ct);
        await store.SaveAsync(settings with { Language = "de" }, Ct);

        settings.ShouldBe(new AppSettings());
        store.LastLoad.Problem.ShouldBe(SettingsLoadProblem.Damaged);
        string copy = store.LastLoad.BackupFile.ShouldNotBeNull();
        Path.GetFileName(copy).ShouldBe("settings.corrupt-20260920-101500.json");
        (await System.IO.File.ReadAllTextAsync(copy, Ct)).ShouldBe(damaged);
    }

    [Fact]
    public async Task Load_ReadableFile_ReportsNoProblemAndKeepsNoCopy()
    {
        await WriteAsync("""{ "schemaVersion": 1, "language": "en" }""");
        JsonSettingsStore store = Store();

        await store.LoadAsync(Ct);

        store.LastLoad.Problem.ShouldBe(SettingsLoadProblem.None);
        store.LastLoad.BackupFile.ShouldBeNull();
        Directory.GetFiles(_directory).ShouldHaveSingleItem();
    }

    /// <summary>A downgrade: saving would drop every key this version does not know.</summary>
    [Fact]
    public async Task Load_FileFromANewerVersion_KeepsACopyAndLoadsWhatItKnows()
    {
        await WriteAsync("""{ "schemaVersion": 7, "language": "en", "somethingNew": { "a": 1 } }""");
        JsonSettingsStore store = Store();

        AppSettings settings = await store.LoadAsync(Ct);

        settings.Language.ShouldBe("en");
        store.LastLoad.Problem.ShouldBe(SettingsLoadProblem.FromNewerVersion);
        Path.GetFileName(store.LastLoad.BackupFile.ShouldNotBeNull()).ShouldBe("settings.v7-20260920-101500.json");
    }

    /// <summary>A file someone else holds open is not damaged, but saving defaults over it would destroy it all the same.</summary>
    [Fact]
    public async Task Load_FileThatIsLocked_IsCopiedBeforeTheFirstSave()
    {
        const string content = """{ "schemaVersion": 1, "onlyNotifyAboutUpdates": true }""";
        await WriteAsync(content);
        JsonSettingsStore store = Store();

        await using (new FileStream(File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            (await store.LoadAsync(Ct)).ShouldBe(new AppSettings());
            store.LastLoad.Problem.ShouldBe(SettingsLoadProblem.Unreadable);
        }

        await store.SaveAsync(new AppSettings { Language = "de" }, Ct);

        string copy = Directory.GetFiles(_directory, "settings.corrupt-*.json").ShouldHaveSingleItem();
        (await System.IO.File.ReadAllTextAsync(copy, Ct)).ShouldBe(content);
    }

    [Fact]
    public async Task Load_ValuesOutOfRange_AreClamped()
    {
        await WriteAsync("""
            {
              "schemaVersion": 1,
              "confirmTimeoutSeconds": -5,
              "language": "not a culture ///",
              "fovDistanceCm": 100000,
              "fovBezelMm": -3,
              "fovAngleDegrees": 400,
              "fovVerticalOffsetCm": -1,
              "fovCurvatureMm": { "a": 1, "b": 1800 },
              "automationRules": [ null, { "profileId": "6f1f7a3e-5a0e-4f0f-9a57-0d7d1a1b2c3d", "devices": [ null, { "id": "VID_046D&PID_C24F" } ], "exitDelaySeconds": 99999 } ],
              "displayNames": { "x": null, "y": "Left" }
            }
            """);

        AppSettings settings = await Store().LoadAsync(Ct);

        settings.ConfirmTimeoutSeconds.ShouldBe(0);
        settings.Language.ShouldBeNull();
        settings.FovDistanceCm.ShouldBe(300);
        settings.FovBezelMm.ShouldBe(0);
        settings.FovAngleDegrees.ShouldBe(89);
        settings.FovVerticalOffsetCm.ShouldBe(0);
        settings.FovCurvatureMm.ShouldNotBeNull()["a"].ShouldBe(300);
        settings.FovCurvatureMm["b"].ShouldBe(1800);
        var rule = settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        rule.ExitDelaySeconds.ShouldBe(Automation.AutomationRule.MaxExitDelaySeconds);
        rule.Devices.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe("VID_046D&PID_C24F");
        settings.DisplayNames.ShouldNotBeNull().Keys.ShouldBe(["y"]);
    }

    [Fact]
    public void Sanitized_WithNothingToFix_IsTheSameInstance()
    {
        var settings = new AppSettings { ConfirmTimeoutSeconds = 20, Language = "de", FovDistanceCm = 70 };

        settings.Sanitized().ShouldBeSameAs(settings);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class FakeTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 20, 10, 15, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
