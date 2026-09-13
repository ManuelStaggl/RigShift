using RigShift.Core.Settings;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class JsonSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private string File => Path.Combine(_directory, "settings.json");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_MissingFile_ReturnsDefaults()
    {
        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.ShouldBe(new AppSettings());
        settings.ConfirmTimeoutSeconds.ShouldBe(15);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var settings = new AppSettings { DefaultProfileId = Guid.NewGuid(), ApplyDefaultProfileOnStartup = true, ConfirmTimeoutSeconds = 20, Language = "de" };

        await store.SaveAsync(settings, Ct);

        (await store.LoadAsync(Ct)).ShouldBe(settings);
    }

    [Fact]
    public async Task Load_BrokenFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, "{ broken", Ct);

        (await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct)).ShouldBe(new AppSettings());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
