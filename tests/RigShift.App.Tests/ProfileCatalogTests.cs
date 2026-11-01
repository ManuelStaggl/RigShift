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
    public async Task Delete_OfTheDefaultProfile_LeavesNoDefault()
    {
        (Profile desk, _) = await TwoProfilesAsync();
        await _host.Catalog.ToggleDefaultAsync(desk, CancellationToken.None);

        await _host.Catalog.DeleteAsync(desk, CancellationToken.None);

        _host.Settings.Current.DefaultProfileId.ShouldBeNull();
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
