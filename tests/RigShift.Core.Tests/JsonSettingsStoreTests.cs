using RigShift.Core.Automation;
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
        var settings = new AppSettings { DefaultProfileId = Guid.NewGuid(), ApplyDefaultProfileOnStartup = true, ConfirmTimeoutSeconds = 20, Language = "de", OnlyNotifyAboutUpdates = true };

        await store.SaveAsync(settings, Ct);

        (await store.LoadAsync(Ct)).ShouldBe(settings);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAutomationRules()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var rule = new AutomationRule
        {
            TemplateId = null,
            ExecutablePath = @"C:\Games\MySim.exe",
            ProfileId = Guid.NewGuid(),
            OnExit = ExitAction.SwitchTo,
            ExitProfileId = Guid.NewGuid(),
            SkipConfirmation = true,
            IsEnabled = false,
        };

        await store.SaveAsync(new AppSettings { AutomationRules = new List<AutomationRule> { rule }, AutomationPaused = true }, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.AutomationPaused.ShouldBeTrue();
        loaded.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(rule);
        (await System.IO.File.ReadAllTextAsync(File, Ct)).ShouldContain("\"switchTo\"", Case.Insensitive);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsUsbRule()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var rule = new AutomationRule { UsbDeviceId = "VID_0EB7&PID_0020", UsbDeviceName = "CSL DD", ProfileId = Guid.NewGuid() };

        await store.SaveAsync(new AppSettings { AutomationRules = new List<AutomationRule> { rule } }, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(rule);
    }

    [Fact]
    public async Task Load_RuleWithoutEnabledKey_IsEnabled()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "automationRules": [ { "templateId": "iracing", "onExit": "SwitchBack" } ] }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        AutomationRule rule = settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        rule.IsEnabled.ShouldBeTrue();
        rule.OnExit.ShouldBe(ExitAction.SwitchBack);
        settings.AutomationPaused.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_FileFromVersion1_0_InstallsUpdatesAutomatically()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "schemaVersion": 1, "confirmTimeoutSeconds": 20 }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.OnlyNotifyAboutUpdates.ShouldBeFalse();
        settings.ConfirmTimeoutSeconds.ShouldBe(20);
    }

    [Fact]
    public async Task Load_FileWithoutConfirmTimeout_KeepsDefaultTimeout()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "schemaVersion": 1 }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.ConfirmTimeoutSeconds.ShouldBe(15);
    }

    [Fact]
    public async Task Load_FileFromVersion1_2_HasHttpApiOffOnDefaultPort()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "schemaVersion": 1, "automationPaused": true }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.HttpApiEnabled.ShouldBeFalse();
        settings.HttpApiPort.ShouldBe(AppSettings.DefaultHttpApiPort);
        settings.HttpApiToken.ShouldBeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsHttpApi()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var settings = new AppSettings { HttpApiEnabled = true, HttpApiPort = 50123, HttpApiToken = "abc" };

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
