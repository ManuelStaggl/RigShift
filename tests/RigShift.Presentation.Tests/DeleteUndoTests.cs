using System.IO;
using RigShift.App.Services;
using RigShift.Core.Automation;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>Deleting leaves a backup behind, and undo brings the profile back with what went with it.</summary>
public sealed class DeleteUndoTests : IDisposable
{
    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string[] Backups => Directory.Exists(AutoBackups.Folder(_host.Paths.DataDirectory))
        ? Directory.GetFiles(AutoBackups.Folder(_host.Paths.DataDirectory), "auto-*.zip")
        : [];

    [Fact]
    public async Task DeletedProfile_ComesBackWithItsRulesExitTargetAndDefault()
    {
        Profile desk = Profile("Desk", DeskModes);
        Profile rig = Rig();
        await _host.Store.SaveAsync(desk, Ct);
        await _host.Store.SaveAsync(rig, Ct);
        var first = new AutomationRule { ProfileId = desk.Id };
        var own = new AutomationRule { ProfileId = rig.Id };
        var endsInRig = new AutomationRule { ProfileId = desk.Id, OnExit = ExitAction.SwitchTo, ExitProfileId = rig.Id };
        await _host.Settings.UpdateAsync(s => s with { SignInProfileId = rig.Id, AutomationRules = [first, own, endsInRig] }, Ct);
        await _host.Catalog.ReloadAsync(Ct);

        DeletedProfile deleted = await _host.Catalog.DeleteAsync(rig, Ct);

        Backups.ShouldHaveSingleItem();
        _host.Catalog.Find(rig.Id).ShouldBeNull();
        _host.Settings.Current.SignInProfileId.ShouldBeNull();

        await _host.Catalog.UndoDeleteAsync(deleted, Ct);

        _host.Catalog.Find(rig.Id).ShouldNotBeNull();
        _host.Settings.Current.SignInProfileId.ShouldBe(rig.Id);
        _host.Settings.Current.AutomationRules.ShouldBe([first, own, endsInRig]);
    }

    [Fact]
    public async Task DeletingAGame_WritesABackupFirst()
    {
        await _host.Store.SaveAsync(Rig(), Ct);
        await _host.Catalog.ReloadAsync(Ct);
        var games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        var game = new GameEntry { Id = Guid.NewGuid(), Name = "iRacing", Launch = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410" } };
        await games.SaveAsync(game, Ct);

        await games.DeleteAsync(game.Id, Ct);

        Backups.ShouldHaveSingleItem();
        games.Games.ShouldBeEmpty();
    }

    [Fact]
    public async Task BackupOnceForVersion_WritesOnlyOncePerVersion()
    {
        await _host.Store.SaveAsync(Rig(), Ct);
        await _host.Catalog.ReloadAsync(Ct);

        await _host.Catalog.BackupOnceForVersionAsync("5.0.0", Ct);
        await _host.Catalog.BackupOnceForVersionAsync("5.0.0", Ct);
        Backups.Length.ShouldBe(1);

        await _host.Catalog.BackupOnceForVersionAsync("5.0.1", Ct);
        Backups.Length.ShouldBe(2);
    }

    public void Dispose() => _host.Dispose();
}
