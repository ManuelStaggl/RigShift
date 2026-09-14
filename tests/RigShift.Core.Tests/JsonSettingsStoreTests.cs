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
        var settings = new AppSettings { DefaultProfileId = Guid.NewGuid(), ConfirmTimeoutSeconds = 20, Language = "de", OnlyNotifyAboutUpdates = true };

        await store.SaveAsync(settings, Ct);

        (await store.LoadAsync(Ct)).ShouldBe(settings);
    }

    [Fact]
    public async Task Load_FileWithRemovedApplyOnStartupKey_KeepsTheOtherValues()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File) ?? ".");
        await System.IO.File.WriteAllTextAsync(File, """
            { "schemaVersion": 1, "applyDefaultProfileOnStartup": true, "confirmTimeoutSeconds": 25, "language": "en" }
            """, Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.ConfirmTimeoutSeconds.ShouldBe(25);
        settings.Language.ShouldBe("en");
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsAutomationRules()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var rule = new AutomationRule
        {
            Devices = [new RuleDevice { Id = "VID_046D&PID_C24F" }],
            ProfileId = Guid.NewGuid(),
            OnExit = ExitAction.SwitchTo,
            ExitProfileId = Guid.NewGuid(),
            SkipConfirmation = true,
            ExitDelaySeconds = 30,
        };

        await store.SaveAsync(new AppSettings { AutomationRules = new List<AutomationRule> { rule }, AutomationPaused = true }, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.AutomationPaused.ShouldBeTrue();
        ShouldBeSameRule(loaded.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem(), rule);
        string json = await System.IO.File.ReadAllTextAsync(File, Ct);
        json.ShouldContain("\"switchTo\"", Case.Insensitive);
        json.ShouldNotContain("isEnabled", Case.Insensitive);
        json.ShouldNotContain("usbDeviceId", Case.Insensitive);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsCombinationAndDeviceNames()
    {
        var store = new JsonSettingsStore(File, Logger.None);
        var rule = new AutomationRule
        {
            Devices = [new RuleDevice { Id = "VID_0EB7&PID_0020", Name = "CSL DD" }, new RuleDevice { Id = "VID_0EB7&PID_0030", Name = "Pedals" }],
            ProfileId = Guid.NewGuid(),
        };
        var names = new Dictionary<string, string> { ["VID_0EB7&PID_0020"] = "Wheel" };

        await store.SaveAsync(new AppSettings { AutomationRules = [rule], UsbDeviceNames = names }, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        ShouldBeSameRule(loaded.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem(), rule);
        loaded.UsbDeviceNames.ShouldNotBeNull()["VID_0EB7&PID_0020"].ShouldBe("Wheel");
    }

    [Fact]
    public async Task Load_RuleFromVersion1_3_MovesItsDeviceIntoTheList()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """
            { "automationRules": [ { "usbDeviceId": "VID_0EB7&PID_0020", "usbDeviceName": "CSL DD" }, { "usbDeviceId": "" } ] }
            """, Ct);
        var store = new JsonSettingsStore(File, Logger.None);

        AppSettings settings = await store.LoadAsync(Ct);

        IReadOnlyList<AutomationRule> rules = settings.AutomationRules.ShouldNotBeNull();
        rules[0].Devices.ShouldNotBeNull().ShouldHaveSingleItem().ShouldBe(new RuleDevice { Id = "VID_0EB7&PID_0020", Name = "CSL DD" });
        rules[0].LegacyUsbDeviceId.ShouldBeNull();
        rules[1].Devices.ShouldNotBeNull().ShouldBeEmpty();
        AutomationTrigger.IsIgnored(rules[1]).ShouldBeFalse();

        await store.SaveAsync(settings, Ct);
        (await System.IO.File.ReadAllTextAsync(File, Ct)).ShouldNotContain("usbDeviceId", Case.Insensitive);
    }

    [Fact]
    public async Task Load_RuleWithoutEnabledKey_IsKept()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "automationRules": [ { "usbDeviceId": "VID_0EB7&PID_0020", "onExit": "SwitchBack" } ] }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        AutomationRule rule = settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        rule.OnExit.ShouldBe(ExitAction.SwitchBack);
        rule.LegacyIsEnabled.ShouldBeNull();
        settings.AutomationPaused.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_DisabledRuleFromOldVersion_IsDroppedAndEnabledRuleKept()
    {
        // 1.3.x wrote "isEnabled"; a rule switched off there must not come back on and switch unexpectedly (finding O-07).
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """
            {
              "automationRules": [
                { "usbDeviceId": "VID_0EB7&PID_0020", "isEnabled": false },
                { "usbDeviceId": "VID_046D&PID_C24F", "isEnabled": true }
              ]
            }
            """, Ct);
        var store = new JsonSettingsStore(File, Logger.None);

        AppSettings settings = await store.LoadAsync(Ct);

        AutomationRule kept = settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        kept.Devices.ShouldNotBeNull().ShouldHaveSingleItem().Id.ShouldBe("VID_046D&PID_C24F");
        kept.LegacyIsEnabled.ShouldBeNull();

        await store.SaveAsync(settings, Ct);
        (await System.IO.File.ReadAllTextAsync(File, Ct)).ShouldNotContain("isEnabled", Case.Insensitive);
    }

    [Fact]
    public async Task Load_RuleWithoutExitDelay_UsesTenSeconds()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "automationRules": [ { "usbDeviceId": "VID_0EB7&PID_0020" } ] }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem().ExitDelaySeconds.ShouldBe(AutomationRule.DefaultExitDelaySeconds);
        AutomationRule.DefaultExitDelaySeconds.ShouldBe(10);
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
    public async Task Load_FileWithKeysOfDroppedFeatures_IgnoresThem()
    {
        // Unreleased builds wrote game rules and HTTP API settings; such files must still load.
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """
            {
              "schemaVersion": 1,
              "confirmTimeoutSeconds": 20,
              "httpApiEnabled": true,
              "httpApiPort": 47800,
              "httpApiToken": "abc",
              "automationRules": [ { "templateId": "iracing", "executablePath": "C:\\Games\\MySim.exe", "onExit": "Stay" } ]
            }
            """, Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.ConfirmTimeoutSeconds.ShouldBe(20);
        AutomationTrigger.IsIgnored(settings.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem()).ShouldBeTrue();
    }

    /// <summary>Record equality compares the device list by reference.</summary>
    private static void ShouldBeSameRule(AutomationRule actual, AutomationRule expected)
    {
        (actual with { Devices = null }).ShouldBe(expected with { Devices = null });
        actual.Devices.ShouldNotBeNull().ShouldBe(expected.Devices.ShouldNotBeNull());
    }

    [Fact]
    public async Task Load_FileWithoutDuckingKeys_HasNoMemory()
    {
        Directory.CreateDirectory(_directory);
        await System.IO.File.WriteAllTextAsync(File, """{ "schemaVersion": 1, "confirmTimeoutSeconds": 20 }""", Ct);

        AppSettings settings = await new JsonSettingsStore(File, Logger.None).LoadAsync(Ct);

        settings.HasDuckingMemory.ShouldBeFalse();
        settings.DuckingBeforeProfiles.ShouldBeNull();
    }

    [Fact]
    public async Task SaveThenLoad_RoundTripsDuckingMemoryWithMissingValue()
    {
        var store = new JsonSettingsStore(File, Logger.None);

        await store.SaveAsync(new AppSettings { HasDuckingMemory = true, DuckingBeforeProfiles = null }, Ct);
        AppSettings loaded = await store.LoadAsync(Ct);

        loaded.HasDuckingMemory.ShouldBeTrue();
        loaded.DuckingBeforeProfiles.ShouldBeNull();
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
