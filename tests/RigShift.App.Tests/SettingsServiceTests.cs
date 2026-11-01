using System.IO;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Abstractions;
using RigShift.Core.Settings;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Update_SaveThrows_KeepsPreviousCurrentAndRaisesNoChange()
    {
        // The settings folder is a file, so writing fails like a read-only or locked data folder.
        Directory.CreateDirectory(_directory);
        string blocker = Path.Combine(_directory, "blocker");
        await File.WriteAllTextAsync(blocker, "not a folder", Ct);
        using var settings = new SettingsService(new JsonSettingsStore(Path.Combine(blocker, "settings.json"), Logger.None), Substitute.For<IAutostart>());
        bool changed = false;
        settings.Changed += (_, _) => changed = true;

        await Should.ThrowAsync<IOException>(() => settings.UpdateAsync(s => s with { ConfirmTimeoutSeconds = 30 }, Ct));

        settings.Current.ConfirmTimeoutSeconds.ShouldBe(15);
        changed.ShouldBeFalse();
    }

    /// <summary>The app asks this at startup to tell the user that defaults replaced the settings.</summary>
    [Fact]
    public async Task Load_DamagedFile_SaysSoAndNamesTheCopy()
    {
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(file, """{ "automationRules": [""", Ct);
        using var settings = new SettingsService(new JsonSettingsStore(file, Logger.None), Substitute.For<IAutostart>());

        await settings.LoadAsync(Ct);

        settings.LastLoad.Problem.ShouldBe(SettingsLoadProblem.Damaged);
        File.Exists(settings.LastLoad.BackupFile.ShouldNotBeNull()).ShouldBeTrue();
    }

    [Fact]
    public async Task Update_WithoutNotify_SavesButRaisesNoChange()
    {
        string file = Path.Combine(_directory, "settings.json");
        using var settings = new SettingsService(new JsonSettingsStore(file, Logger.None), Substitute.For<IAutostart>());
        bool changed = false;
        settings.Changed += (_, _) => changed = true;

        await new SettingsDuckingMemory(settings).SaveAsync(CommunicationsDucking.ReduceBy50Percent, Ct);

        changed.ShouldBeFalse();
        (await new JsonSettingsStore(file, Logger.None).LoadAsync(Ct)).DuckingBeforeProfiles.ShouldBe(CommunicationsDucking.ReduceBy50Percent);
        (await new SettingsDuckingMemory(settings).LoadAsync(Ct)).ShouldBe(new RememberedDucking(CommunicationsDucking.ReduceBy50Percent));
    }

    [Fact]
    public async Task Reload_ReadsTheReplacedFileAndRaisesChanged()
    {
        string file = Path.Combine(_directory, "settings.json");
        using var settings = new SettingsService(new JsonSettingsStore(file, Logger.None), Substitute.For<IAutostart>());
        await settings.UpdateAsync(s => s with { ConfirmTimeoutSeconds = 30 }, Ct);
        bool changed = false;
        settings.Changed += (_, _) => changed = true;

        // A restored backup replaces the file behind the service's back.
        await new JsonSettingsStore(file, Logger.None).SaveAsync(new AppSettings { ConfirmTimeoutSeconds = 5, Language = "en" }, Ct);
        await settings.ReloadAsync(Ct);

        changed.ShouldBeTrue();
        settings.Current.ConfirmTimeoutSeconds.ShouldBe(5);
        settings.Current.Language.ShouldBe("en");
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
