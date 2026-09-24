using System.IO;
using System.Net.Http;
using NSubstitute;
using RigShift.App.Services;
using RigShift.Core.Tests.Fakes;
using RigShift.Core.Updates;
using Serilog.Core;
using Shouldly;
using Velopack;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class UpdateServiceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly AppTestHost _host = new(new FakeDisplayConfigurator(DeskActive()));
    private readonly ScriptedFeed _feed = new();
    private readonly IUpdatePolicy _policy = Substitute.For<IUpdatePolicy>();
    private readonly IAppShell _shell = Substitute.For<IAppShell>();
    private readonly StepClock _clock = new();
    private readonly List<string> _ready = [];
    private readonly List<string> _available = [];
    private UpdateService? _service;

    public void Dispose()
    {
        _service?.Dispose();
        _host.Dispose();
    }

    private UpdateService Service()
    {
        _service = new UpdateService(_feed, _policy, _host.Coordinator, _host.Settings, _shell, _clock, Logger.None);
        _service.UpdateReady += (_, version) => _ready.Add(version);
        _service.UpdateAvailable += (_, version) => _available.Add(version);
        return _service;
    }

    private Task OnlyNotifyAsync(bool value) =>
        _host.Settings.UpdateAsync(s => s with { OnlyNotifyAboutUpdates = value }, Ct);

    private static UpdateInfo Release(string version, string notes = "- Fixed the thing") => new(
        new VelopackAsset { PackageId = "RigShift", Version = SemanticVersion.Parse(version), NotesMarkdown = notes },
        false);

    private static async Task UntilAsync(Func<bool> condition, string what)
    {
        DateTime giveUp = DateTime.UtcNow + Patience;
        while (!condition())
        {
            if (DateTime.UtcNow > giveUp)
            {
                throw new TimeoutException(what);
            }

            await Task.Delay(10, Ct);
        }
    }

    [Fact]
    public async Task DevelopmentBuild_NeverChecks()
    {
        _feed.IsInstalled = false;
        UpdateService service = Service();

        service.Start();
        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.NotInstalled);
        service.CanCheck.ShouldBeFalse();
        service.CanInstallNow.ShouldBeFalse();
        _feed.Checks.ShouldBe(0);
        _clock.Pending.ShouldBe(0);
    }

    [Fact]
    public async Task ChecksDisabledByPolicy_NeverChecks()
    {
        _policy.ChecksDisabled.Returns(true);
        UpdateService service = Service();

        service.Start();
        await service.CheckNowAsync();
        service.Resumed();

        service.State.ShouldBe(UpdateState.DisabledByPolicy);
        service.CanCheck.ShouldBeFalse();
        service.CanInstallNow.ShouldBeFalse();
        _feed.Checks.ShouldBe(0);
        _clock.Pending.ShouldBe(0);
    }

    [Theory]
    [InlineData(1, null, true)]
    [InlineData(null, 1, true)]
    [InlineData("1", null, true)]
    [InlineData(0, 1, false)]
    [InlineData(0, null, false)]
    [InlineData(null, null, false)]
    [InlineData("yes", null, false)]
    public void Policy_MachineValueWinsOverUserValue(object? machine, object? user, bool disabled) =>
        RegistryUpdatePolicy.IsDisabled(machine, user).ShouldBe(disabled);

    [Fact]
    public async Task Check_NothingNewer_IsUpToDate()
    {
        UpdateService service = Service();
        service.State.ShouldBe(UpdateState.NotChecked);
        service.CurrentVersion.ShouldBe("3.5.1");

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.UpToDate);
        service.TargetVersion.ShouldBeNull();
        service.ReleaseUrl.ShouldBeNull();
        service.LastChecked.ShouldNotBeNull();
        service.CanCheck.ShouldBeTrue();
    }

    [Fact]
    public async Task Check_NewerVersion_IsDownloadedAndReported()
    {
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();
        var states = new List<UpdateState>();
        service.StateChanged += (_, _) => states.Add(service.State);

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Ready);
        service.TargetVersion.ShouldBe("9.9.9");
        service.ReleaseUrl.ShouldBe("https://github.com/ManuelStaggl/RigShift/releases/tag/v9.9.9");
        service.ReleaseNoteLines.ShouldNotBeEmpty();
        service.CanInstallNow.ShouldBeTrue();
        states.ShouldBe([UpdateState.Checking, UpdateState.Downloading, UpdateState.Ready]);
        _feed.Downloads.ShouldBe(1);
        _ready.ShouldBe(["9.9.9"]);
        _available.ShouldBeEmpty();
    }

    /// <summary>The daily check finds the version that already waits for the next start; nothing to load or report again.</summary>
    [Fact]
    public async Task Check_VersionAlreadyDownloaded_StaysReadyWithoutAnotherDownload()
    {
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();
        await service.CheckNowAsync();

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Ready);
        _feed.Downloads.ShouldBe(1);
        _ready.ShouldBe(["9.9.9"]);
    }

    [Fact]
    public async Task Check_OnlyNotify_ReportsOnceAndLoadsNothing()
    {
        await OnlyNotifyAsync(true);
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();

        await service.CheckNowAsync();
        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Available);
        service.TargetVersion.ShouldBe("9.9.9");
        service.CanInstallNow.ShouldBeTrue();
        _feed.Downloads.ShouldBe(0);
        _available.ShouldBe(["9.9.9"]);
        _ready.ShouldBeEmpty();
    }

    [Fact]
    public async Task OnlyNotify_SwitchedOffWhileAVersionIsReported_LoadsItRightAway()
    {
        await OnlyNotifyAsync(true);
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();
        await service.CheckNowAsync();

        await OnlyNotifyAsync(false);

        await UntilAsync(() => service.State == UpdateState.Ready, "The reported version should have been downloaded.");
        _feed.Downloads.ShouldBe(1);
    }

    [Fact]
    public async Task Check_FeedUnreachable_Fails_AndCanBeTriedAgain()
    {
        _feed.CheckFails = true;
        UpdateService service = Service();

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Failed);
        service.LastChecked.ShouldBeNull();
        service.CanCheck.ShouldBeTrue();

        _feed.CheckFails = false;
        await service.CheckNowAsync();
        service.State.ShouldBe(UpdateState.UpToDate);
    }

    /// <summary>A downloaded update is still there when GitHub is not; the card must keep offering it.</summary>
    [Fact]
    public async Task Check_FailsWhileAnUpdateIsReady_StaysReady()
    {
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();
        await service.CheckNowAsync();
        _feed.CheckFails = true;

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Ready);
        service.TargetVersion.ShouldBe("9.9.9");
        service.CanInstallNow.ShouldBeTrue();
    }

    [Fact]
    public async Task Download_Fails_IsReportedAsFailed()
    {
        _feed.Next = Release("9.9.9");
        _feed.DownloadFails = true;
        UpdateService service = Service();

        await service.CheckNowAsync();

        service.State.ShouldBe(UpdateState.Failed);
        service.CanInstallNow.ShouldBeFalse();
        service.CanCheck.ShouldBeTrue();
        _ready.ShouldBeEmpty();
    }

    [Fact]
    public async Task InstallNow_ReportedVersion_LoadsItStartsTheUpdaterAndQuits()
    {
        await OnlyNotifyAsync(true);
        _feed.Next = Release("9.9.9");
        UpdateService service = Service();
        await service.CheckNowAsync();

        await service.InstallNowAsync();

        _feed.Downloads.ShouldBe(1);
        _feed.Applied.ShouldBe(["9.9.9"]);

        // The user's exit with its questions: a hidden editor with unsaved changes, a running game (A-14, E-06).
        _shell.Received(1).QuitByUser();
        _shell.DidNotReceive().Quit();
    }

    [Fact]
    public async Task InstallNow_DownloadFails_DoesNotQuit()
    {
        await OnlyNotifyAsync(true);
        _feed.Next = Release("9.9.9");
        _feed.DownloadFails = true;
        UpdateService service = Service();
        await service.CheckNowAsync();

        await service.InstallNowAsync();

        service.State.ShouldBe(UpdateState.Failed);
        _feed.Applied.ShouldBeEmpty();
        _shell.DidNotReceive().Quit();
        _shell.DidNotReceive().QuitByUser();
    }

    [Fact]
    public async Task InstallNow_UpdaterCannotBeStarted_FailsAndKeepsRunning()
    {
        _feed.Next = Release("9.9.9");
        _feed.ApplyFails = true;
        UpdateService service = Service();
        await service.CheckNowAsync();

        await service.InstallNowAsync();

        service.State.ShouldBe(UpdateState.Failed);
        _shell.DidNotReceive().Quit();
        _shell.DidNotReceive().QuitByUser();
    }

    [Fact]
    public async Task InstallNow_NothingToInstall_DoesNothing()
    {
        UpdateService service = Service();
        await service.CheckNowAsync();

        await service.InstallNowAsync();

        _feed.Applied.ShouldBeEmpty();
        _shell.DidNotReceive().Quit();
        _shell.DidNotReceive().QuitByUser();
    }

    /// <summary>The check at login often runs before the network is up; a PC that is off every night must still update.</summary>
    [Fact]
    public async Task Start_FailedChecks_AreTriedAgainAfter1_5And30Minutes_ThenDaily()
    {
        _feed.CheckFails = true;
        UpdateService service = Service();

        service.Start();

        foreach (int minutes in new[] { 1, 5, 30 })
        {
            int checks = _feed.Checks;
            await UntilAsync(() => _clock.Pending == 1, "The next check should be scheduled.");
            _clock.NextDueIn.ShouldBe(TimeSpan.FromMinutes(minutes));
            _clock.Advance(TimeSpan.FromMinutes(minutes));
            await UntilAsync(() => _feed.Checks == checks + 1, "The check should have been tried again.");
        }

        await UntilAsync(() => _clock.Pending == 1, "The next check should be scheduled.");
        _clock.NextDueIn.ShouldBe(UpdateSchedule.Regular);

        _feed.CheckFails = false;
        _clock.Advance(UpdateSchedule.Regular);
        await UntilAsync(() => service.State == UpdateState.UpToDate, "The daily check should have succeeded.");
        await UntilAsync(() => _clock.Pending == 1, "The next check should be scheduled.");
        _clock.NextDueIn.ShouldBe(UpdateSchedule.Regular);
    }

    [Fact]
    public async Task Resume_AfterAFailedCheck_ChecksNow()
    {
        _feed.CheckFails = true;
        UpdateService service = Service();
        service.Start();
        await UntilAsync(() => _clock.Pending == 1, "The retry should be scheduled.");
        _feed.CheckFails = false;

        service.Resumed();

        await UntilAsync(() => service.State == UpdateState.UpToDate, "Waking up should have checked right away.");
        _feed.Checks.ShouldBe(2);
    }

    [Fact]
    public async Task Resume_ShortlyAfterAGoodCheck_DoesNotCheckAgain()
    {
        UpdateService service = Service();
        service.Start();
        await UntilAsync(() => _clock.Pending == 1, "The daily check should be scheduled.");

        _clock.Advance(TimeSpan.FromHours(2));
        service.Resumed();
        await Task.Delay(150, Ct);

        _feed.Checks.ShouldBe(1);
    }

    [Fact]
    public async Task Resume_ADayAfterTheLastGoodCheck_ChecksNow()
    {
        UpdateService service = Service();
        service.Start();
        await UntilAsync(() => _clock.Pending == 1, "The daily check should be scheduled.");

        _clock.AdvanceWithoutFiring(TimeSpan.FromHours(25));
        service.Resumed();

        await UntilAsync(() => _feed.Checks == 2, "A check was due when the PC woke up.");
    }

    private sealed class ScriptedFeed : IUpdateFeed
    {
        private int _checks;
        private int _downloads;

        public bool IsInstalled { get; set; } = true;

        public string? InstalledVersion => IsInstalled ? "3.5.1" : null;

        public UpdateInfo? Next { get; set; }

        public volatile bool CheckFails;

        public bool DownloadFails { get; set; }

        public bool ApplyFails { get; set; }

        public int Checks => Volatile.Read(ref _checks);

        public int Downloads => Volatile.Read(ref _downloads);

        public List<string> Applied { get; } = [];

        public Task<UpdateInfo?> CheckAsync()
        {
            Interlocked.Increment(ref _checks);
            return CheckFails
                ? Task.FromException<UpdateInfo?>(new HttpRequestException("no network"))
                : Task.FromResult(Next);
        }

        public Task DownloadAsync(UpdateInfo update, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _downloads);
            return DownloadFails ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
        }

        public void ApplyAfterExit(VelopackAsset release)
        {
            if (ApplyFails)
            {
                throw new InvalidOperationException("Update.exe is missing");
            }

            Applied.Add(release.Version.ToString());
        }
    }

    /// <summary>A clock that stands still until the test moves it; timers fire when it passes them.</summary>
    private sealed class StepClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ScheduledTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 9, 20, 8, 0, 0, TimeSpan.Zero);

        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Count;
                }
            }
        }

        public TimeSpan NextDueIn
        {
            get
            {
                lock (_gate)
                {
                    return _timers.Min(t => t.Due) - _now;
                }
            }
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate)
            {
                return _now;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ScheduledTimer(this, callback, state);
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                lock (_gate)
                {
                    timer.Due = _now + dueTime;
                    _timers.Add(timer);
                }
            }

            return timer;
        }

        public void Advance(TimeSpan by)
        {
            List<ScheduledTimer> due;
            lock (_gate)
            {
                _now += by;
                due = [.. _timers.Where(t => t.Due <= _now)];
                _timers.RemoveAll(due.Contains);
            }

            due.ForEach(t => t.Fire());
        }

        /// <summary>The PC slept: time passed, but no timer ran.</summary>
        public void AdvanceWithoutFiring(TimeSpan by)
        {
            lock (_gate)
            {
                _now += by;
            }
        }

        private void Remove(ScheduledTimer timer)
        {
            lock (_gate)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ScheduledTimer(StepClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset Due { get; set; }

            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => clock.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
