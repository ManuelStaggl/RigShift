using System.IO;
using RigShift.Core.Automation;
using RigShift.Core.Profiles;
using RigShift.Core.Tests.Fakes;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class ProfileCatalogTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    [Fact]
    public async Task Delete_TakesTheRulesOfTheProfileWithIt()
    {
        (Profile desk, Profile rig) = await TwoProfilesAsync();
        AutomationRule ofRig = new() { ProfileId = rig.Id, OnExit = ExitAction.SwitchTo, ExitProfileId = desk.Id };
        AutomationRule ofDesk = new() { ProfileId = desk.Id, OnExit = ExitAction.SwitchBack };
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = [ofRig, ofDesk] }, CancellationToken.None);

        _host.Catalog.RuleCount(rig.Id).ShouldBe(1);
        await _host.Catalog.DeleteAsync(rig, CancellationToken.None);

        AutomationRule left = _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        left.Id.ShouldBe(ofDesk.Id);
    }

    [Fact]
    public async Task Delete_LeavesARuleOfAnotherProfileAlone_ButNotItsEndAction()
    {
        (Profile desk, Profile rig) = await TwoProfilesAsync();
        AutomationRule ofDesk = new() { ProfileId = desk.Id, OnExit = ExitAction.SwitchTo, ExitProfileId = rig.Id };
        await _host.Settings.UpdateAsync(s => s with { AutomationRules = [ofDesk] }, CancellationToken.None);

        _host.Catalog.RuleCount(rig.Id).ShouldBe(0);
        await _host.Catalog.DeleteAsync(rig, CancellationToken.None);

        AutomationRule left = _host.Settings.Current.AutomationRules.ShouldNotBeNull().ShouldHaveSingleItem();
        left.Id.ShouldBe(ofDesk.Id);
        left.OnExit.ShouldBe(ExitAction.Stay);
        left.ExitProfileId.ShouldBeNull();
    }

    [Fact]
    public async Task Delete_OfTheSignInProfile_KeepsWhatWindowsRestores()
    {
        (Profile desk, _) = await TwoProfilesAsync();
        await _host.Settings.UpdateAsync(s => s with { SignInProfileId = desk.Id }, CancellationToken.None);

        await _host.Catalog.DeleteAsync(desk, CancellationToken.None);

        _host.Settings.Current.SignInProfileId.ShouldBeNull();
    }

    [Theory]
    [InlineData(0, true, 15)]
    [InlineData(20, false, 20)]
    public async Task Reload_ConfirmationOffBefore5_0_MovesIntoTheProfilesOnce(int seconds, bool withoutAsking, int secondsAfter)
    {
        // A 4.x file: the global "confirm after switching" off is a countdown of 0.
        Directory.CreateDirectory(Path.GetDirectoryName(_host.Paths.SettingsFile)!);
        await File.WriteAllTextAsync(_host.Paths.SettingsFile, $$"""{ "schemaVersion": 1, "confirmTimeoutSeconds": {{seconds}} }""", CancellationToken.None);
        await _host.Settings.LoadAsync(CancellationToken.None);
        Profile asks = new() { Id = Guid.NewGuid(), Name = "Rig", Displays = [] };
        Profile silent = new() { Id = Guid.NewGuid(), Name = "Desk", Displays = [], SwitchWithoutAsking = true };
        _host.Store.Profiles.AddRange([asks, silent]);

        await _host.Catalog.ReloadAsync(CancellationToken.None);

        _host.Store.Profiles.Single(p => p.Id == asks.Id).SwitchWithoutAsking.ShouldBe(withoutAsking);
        _host.Catalog.Find(asks.Id).ShouldNotBeNull().SwitchWithoutAsking.ShouldBe(withoutAsking);
        _host.Store.Profiles.Single(p => p.Id == silent.Id).SwitchWithoutAsking.ShouldBeTrue();
        _host.Settings.Current.ConfirmTimeoutSeconds.ShouldBe(secondsAfter);

        // Once moved, a profile the user switches back to asking stays that way.
        await _host.Catalog.SaveAsync(_host.Catalog.Find(asks.Id)! with { SwitchWithoutAsking = false }, CancellationToken.None);
        await _host.Catalog.ReloadAsync(CancellationToken.None);
        _host.Store.Profiles.Single(p => p.Id == asks.Id).SwitchWithoutAsking.ShouldBeFalse();
    }

    private async Task<(Profile Desk, Profile Rig)> TwoProfilesAsync()
    {
        Profile desk = new() { Id = Guid.NewGuid(), Name = "Desk", Displays = [] };
        Profile rig = new() { Id = Guid.NewGuid(), Name = "Rig", Displays = [] };
        await _host.Catalog.SaveAsync(desk, CancellationToken.None);
        await _host.Catalog.SaveAsync(rig, CancellationToken.None);
        return (desk, rig);
    }

    public void Dispose() => _host.Dispose();
}
