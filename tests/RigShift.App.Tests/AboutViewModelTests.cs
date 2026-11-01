using System.IO;
using NSubstitute;
using RigShift.App.Localization;
using RigShift.App.Services;
using RigShift.App.ViewModels;
using RigShift.Core.Abstractions;
using RigShift.Core.Settings;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

/// <summary>Restoring a backup replaces profiles, games and settings, so every way out of the flow is pinned here.</summary>
public sealed class AboutViewModelTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly IHelpPageDialogs _dialogs = Substitute.For<IHelpPageDialogs>();
    private readonly string _backup = Path.Combine(Path.GetTempPath(), "rigshift-app-tests", Guid.NewGuid().ToString("N") + ".zip");

    public void Dispose()
    {
        File.Delete(_backup);
        _host.Dispose();
    }

    private AboutViewModel ViewModel()
    {
        var updates = new UpdateService(
            Substitute.For<IUpdateFeed>(), Substitute.For<IUpdatePolicy>(), _host.Coordinator, _host.Settings,
            Substitute.For<IAppShell>(), TimeProvider.System, Logger.None);
        var games = new GameCatalog(new InMemoryGameStore(), _host.Catalog, Logger.None);
        return new AboutViewModel(
            updates, _host.Coordinator, _host.Catalog, _host.Display, Substitute.For<IAudioController>(), _host.Paths,
            _host.Settings, games, _dialogs, Logger.None);
    }

    /// <summary>A backup with settings only: a confirm timeout of 7 s tells restored settings from the default.</summary>
    private async Task WriteBackupAsync()
    {
        string source = _backup + "-source";
        await new JsonSettingsStore(Path.Combine(source, "settings.json"), Logger.None).SaveAsync(new AppSettings { ConfirmTimeoutSeconds = 7 }, Ct);
        using (FileStream stream = File.Create(_backup))
        {
            BackupArchive.Write(source, stream);
        }

        Directory.Delete(source, recursive: true);
        _dialogs.PickZipToOpen().Returns(_backup);
    }

    [Fact]
    public async Task Restore_DoesNothingWhenThePickIsCancelled()
    {
        _dialogs.PickZipToOpen().Returns((string?)null);
        AboutViewModel vm = ViewModel();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        vm.BackupStatus.ShouldBeNull();
        await _dialogs.DidNotReceiveWithAnyArgs().ConfirmRestoreAsync(default!);
    }

    [Fact]
    public async Task Restore_LeavesTheDataAloneWhenTheQuestionIsDeclined()
    {
        await WriteBackupAsync();
        _dialogs.ConfirmRestoreAsync(Arg.Any<BackupContent>()).Returns(false);
        AboutViewModel vm = ViewModel();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        File.Exists(_host.Paths.SettingsFile).ShouldBeFalse();
        vm.BackupStatus.ShouldBeNull();
    }

    [Fact]
    public async Task Restore_IsRefusedWhileASwitchRuns()
    {
        AboutViewModel vm = ViewModel();
        _host.Coordinator.IsSwitching = true;

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        vm.BackupStatus.ShouldBe(Loc.Instance["About_BackupBusy"]);
        _dialogs.DidNotReceive().PickZipToOpen();
    }

    [Fact]
    public async Task Restore_ReplacesTheSettingsAndSaysSo()
    {
        await WriteBackupAsync();
        _dialogs.ConfirmRestoreAsync(Arg.Any<BackupContent>()).Returns(true);
        AboutViewModel vm = ViewModel();

        await vm.RestoreBackupCommand.ExecuteAsync(null);

        _host.Settings.Current.ConfirmTimeoutSeconds.ShouldBe(7);
        vm.BackupStatus.ShouldBe(Loc.Format("About_BackupRestored", 0));
    }

    [Fact]
    public async Task Restore_ReloadsEverythingEvenWhenItFailsHalfway()
    {
        await WriteBackupAsync();
        _dialogs.ConfirmRestoreAsync(Arg.Any<BackupContent>()).Returns(true);
        AboutViewModel vm = ViewModel();

        // A read-only settings file cannot be replaced; a profile saved meanwhile shows whether the catalog was reloaded.
        Directory.CreateDirectory(_host.Paths.DataDirectory);
        await File.WriteAllTextAsync(_host.Paths.SettingsFile, "{}", Ct);
        File.SetAttributes(_host.Paths.SettingsFile, FileAttributes.ReadOnly);
        await _host.Store.SaveAsync(Profile("Saved meanwhile", DeskModes), Ct);
        try
        {
            await vm.RestoreBackupCommand.ExecuteAsync(null);
        }
        finally
        {
            File.SetAttributes(_host.Paths.SettingsFile, FileAttributes.Normal);
        }

        vm.BackupStatus.ShouldNotBeNull().ShouldNotBe(Loc.Format("About_BackupRestored", 0));
        _host.Catalog.Profiles.ShouldContain(p => p.Name == "Saved meanwhile");
    }
}
