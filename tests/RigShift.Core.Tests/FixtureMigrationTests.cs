using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>
/// Files as the released versions wrote them (shape from <c>git show v1.0.0</c> / <c>v1.2.0</c>, device data invented).
/// Unlike the round-trip tests they do not come from today's serializer, so a renamed key shows up here.
/// </summary>
public sealed class FixtureMigrationTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Load_ProfileFixture1_0_LoadsWithAllDefaults()
    {
        Profile profile = await LoadProfileAsync("profile-1.0.json");

        profile.Id.ShouldBe(Guid.Parse("5d0c2f7e-3c7b-4a7e-9d8b-6a1f0e2b4c11"));
        profile.Name.ShouldBe("Rig");
        profile.Icon.ShouldBe("rig");
        // Own confirmation time 15 s (analysis decision O-01): the profile asks, with the seconds of the app setting.
        profile.SwitchWithoutAsking.ShouldBeFalse();
        profile.ConfirmTimeoutSeconds.ShouldBeNull();
        profile.Displays.Count.ShouldBe(2);

        DisplayAssignment ultrawide = profile.Displays[0];
        ultrawide.Identity.AdapterDevicePath.ShouldBe(@"\\?\PCI#VEN_10DE&DEV_0000#FIXTURE#1");
        ultrawide.Identity.TargetDevicePath.ShouldBe(@"\\?\DISPLAY#SAM0001#FIXTURE&1");
        ultrawide.Identity.EdidManufacturerId.ShouldBe((ushort)19501);
        ultrawide.Identity.EdidProductCodeId.ShouldBe((ushort)1);
        ultrawide.Identity.FriendlyName.ShouldBe("Odyssey G93SC");
        ultrawide.Width.ShouldBe(5120);
        ultrawide.Height.ShouldBe(1440);
        ultrawide.RefreshNumerator.ShouldBe(240000u);
        ultrawide.RefreshDenominator.ShouldBe(1000u);
        ultrawide.PositionX.ShouldBe(0);
        ultrawide.PositionY.ShouldBe(0);
        ultrawide.Rotation.ShouldBe(DisplayRotation.Identity);
        ultrawide.IsPrimary.ShouldBeTrue();
        ultrawide.IsOptional.ShouldBeFalse();
        ultrawide.CustomName.ShouldBeNull();
        ultrawide.Hdr.ShouldBeNull();

        DisplayAssignment tablet = profile.Displays[1];
        tablet.Rotation.ShouldBe(DisplayRotation.Rotate90);
        tablet.IsOptional.ShouldBeTrue();
        tablet.PositionX.ShouldBe(5120);
        tablet.Identity.FriendlyName.ShouldBe(string.Empty);

        profile.Audio.Playback.ShouldBe(new AudioEndpoint("{0.0.0.00000000}.{00000000-0000-0000-0000-00000000f001}", "Headset"));
        profile.Audio.Recording.ShouldBe(new AudioEndpoint("{0.0.1.00000000}.{00000000-0000-0000-0000-00000000f002}", "Headset microphone"));
        profile.Audio.PlaybackCommunications.ShouldBeNull();
        profile.Audio.RecordingCommunications.ShouldBeNull();
        profile.Audio.PlaybackVolumePercent.ShouldBeNull();
        profile.Audio.RecordingVolumePercent.ShouldBeNull();

        // Everything added after 1.0 falls back to its default.
        profile.Hotkey.ShouldBeNull();
        profile.Apps.ShouldNotBeNull().ShouldBeEmpty();
        profile.KeepAwake.ShouldBeFalse();
        profile.AppsWaitForUsbDeviceId.ShouldBeNull();
        profile.AppsWaitForUsbDeviceName.ShouldBeNull();
        profile.AppsWaitSeconds.ShouldBe(Profile.AppsDeviceWaitSeconds);
        profile.DisableCommunicationsDucking.ShouldBeFalse();
    }

    [Fact]
    public async Task Load_ProfileFixture1_2_KeepsHotkeyAndDefaultsTheRest()
    {
        Profile profile = await LoadProfileAsync("profile-1.2.json");

        profile.Id.ShouldBe(Guid.Parse("8f3a1b2c-4d5e-4f60-8a9b-0c1d2e3f4a5b"));
        profile.Name.ShouldBe("Desk");
        profile.Icon.ShouldBeNull();
        profile.SwitchWithoutAsking.ShouldBeFalse();
        profile.ConfirmTimeoutSeconds.ShouldBeNull();
        profile.Hotkey.ShouldBe(new Hotkey { Modifiers = HotkeyModifiers.Control | HotkeyModifiers.Alt, VirtualKey = 0x70 });

        DisplayAssignment desk = profile.Displays.ShouldHaveSingleItem();
        desk.Identity.FriendlyName.ShouldBe("XG32UCWG");
        desk.RefreshNumerator.ShouldBe(164990u);
        desk.RefreshDenominator.ShouldBe(1000u);
        desk.IsPrimary.ShouldBeTrue();
        desk.CustomName.ShouldBeNull();
        desk.Hdr.ShouldBeNull();

        profile.Audio.Playback.ShouldBeNull();
        profile.Audio.Recording.ShouldBeNull();
        profile.Apps.ShouldNotBeNull().ShouldBeEmpty();
        profile.KeepAwake.ShouldBeFalse();
        profile.AppsWaitForUsbDeviceId.ShouldBeNull();
        profile.AppsWaitSeconds.ShouldBe(Profile.AppsDeviceWaitSeconds);
        profile.DisableCommunicationsDucking.ShouldBeFalse();
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("30", false)]
    [InlineData("null", false)]
    public async Task Load_ProfileWithOwnConfirmTime_MigratesToSwitchWithoutAsking(string seconds, bool withoutAsking)
    {
        Directory.CreateDirectory(_directory);
        string json = (await File.ReadAllTextAsync(Fixture("profile-1.0.json"), Ct))
            .Replace("\"confirmTimeoutSeconds\": 15", "\"confirmTimeoutSeconds\": " + seconds, StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(_directory, "profile.json"), json, Ct);
        var store = new JsonProfileStore(_directory, Logger.None);

        Profile profile = (await store.LoadAllAsync(Ct)).Profiles.ShouldHaveSingleItem();
        profile.SwitchWithoutAsking.ShouldBe(withoutAsking);
        profile.ConfirmTimeoutSeconds.ShouldBeNull();

        // Saved again, the old key is gone and the flag stays.
        await store.SaveAsync(profile, Ct);
        string saved = await File.ReadAllTextAsync(Path.Combine(_directory, profile.Id.ToString("D") + ".json"), Ct);
        saved.ShouldNotContain("confirmTimeoutSeconds");
        File.Delete(Path.Combine(_directory, "profile.json"));
        (await store.LoadAllAsync(Ct)).Profiles.ShouldHaveSingleItem().SwitchWithoutAsking.ShouldBe(withoutAsking);
    }

    [Fact]
    public async Task Load_SettingsFixture1_0_KeepsValuesAndDefaultsTheRest()
    {
        Directory.CreateDirectory(_directory);
        string file = Path.Combine(_directory, "settings.json");
        File.Copy(Fixture("settings-1.0.json"), file);

        AppSettings settings = await new JsonSettingsStore(file, Logger.None).LoadAsync(Ct);

        settings.SchemaVersion.ShouldBe(1);
        settings.DefaultProfileId.ShouldBe(Guid.Parse("5d0c2f7e-3c7b-4a7e-9d8b-6a1f0e2b4c11"));
        settings.ApplyDefaultProfileOnStartup.ShouldBeTrue();
        settings.ConfirmTimeoutSeconds.ShouldBe(20);
        settings.Language.ShouldBe("de");
        settings.OnlyNotifyAboutUpdates.ShouldBeFalse();
        settings.DisplayNames.ShouldBeNull();
        settings.AutomationRules.ShouldBeNull();
        settings.AutomationPaused.ShouldBeFalse();
        settings.HasDuckingMemory.ShouldBeFalse();
        settings.DuckingBeforeProfiles.ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private async Task<Profile> LoadProfileAsync(string fixture)
    {
        Directory.CreateDirectory(_directory);
        File.Copy(Fixture(fixture), Path.Combine(_directory, fixture));
        return (await new JsonProfileStore(_directory, Logger.None).LoadAllAsync(Ct)).Profiles.ShouldHaveSingleItem();
    }
}
