using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class GameProcessLearnerTests
{
    private const string InstallFolder = @"D:\Steam\steamapps\common\iRacing";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static RunningProcess Process(int id, string name, string? path, int startedSecondsIn = 0)
        => new(id, name, path, new DateTimeOffset(2026, 9, 16, 12, 0, 0, TimeSpan.Zero).AddSeconds(startedSecondsIn));

    [Fact]
    public async Task Learn_PicksTheProcessThatRunsFromTheGamesFolder()
    {
        var processes = new FakeGameProcesses();
        processes.Running.Add(Process(1, "steam", @"C:\Program Files (x86)\Steam\steam.exe"));
        var learner = new GameProcessLearner(processes, new AutoAdvanceTimeProvider(), Logger.None);
        IReadOnlySet<int> before = learner.Snapshot();

        // The store client opens a window of its own, then the game shows up.
        processes.Running.Add(Process(2, "steamwebhelper", @"C:\Program Files (x86)\Steam\bin\steamwebhelper.exe"));
        processes.Running.Add(Process(3, "iRacingSim64DX11", InstallFolder + @"\iRacingSim64DX11.exe", startedSecondsIn: 5));

        RunningProcess? game = await learner.LearnAsync(before, InstallFolder, Ct);

        game.ShouldNotBeNull();
        game.Name.ShouldBe("iRacingSim64DX11");
    }

    /// <summary>rFactor 2, AMS2 and DCS put a launcher in front: the first new process is the wrong one.</summary>
    [Fact]
    public async Task Learn_SkipsAPreLauncherThatEndsWhenTheGameStarts()
    {
        var processes = new FakeGameProcesses();
        var learner = new GameProcessLearner(processes, new AutoAdvanceTimeProvider(), Logger.None);
        IReadOnlySet<int> before = learner.Snapshot();
        processes.Running.Add(Process(10, "rFactor2 Launcher", InstallFolder + @"\Launcher.exe", startedSecondsIn: 1));

        processes.OnListed = call =>
        {
            if (call == 3)
            {
                processes.Running.RemoveAll(p => p.Id == 10);
                processes.Running.Add(Process(11, "rFactor2", InstallFolder + @"\Bin64\rFactor2.exe", startedSecondsIn: 8));
            }
        };

        RunningProcess? game = await learner.LearnAsync(before, InstallFolder, Ct);

        game.ShouldNotBeNull();
        game.Name.ShouldBe("rFactor2");
    }

    [Fact]
    public async Task Learn_IgnoresProcessesThatWereAlreadyRunning()
    {
        var processes = new FakeGameProcesses();
        processes.Running.Add(Process(3, "iRacingSim64DX11", InstallFolder + @"\iRacingSim64DX11.exe"));
        var learner = new GameProcessLearner(processes, new AutoAdvanceTimeProvider(), Logger.None);
        IReadOnlySet<int> before = learner.Snapshot();

        (await learner.LearnAsync(before, InstallFolder, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Learn_GivesUpWhenNothingShowsUp()
    {
        var processes = new FakeGameProcesses();
        var time = new AutoAdvanceTimeProvider();
        var learner = new GameProcessLearner(processes, time, Logger.None);

        (await learner.LearnAsync(learner.Snapshot(), InstallFolder, Ct)).ShouldBeNull();

        time.Elapsed.ShouldBeGreaterThanOrEqualTo(GameProcessLearner.Timeout);
    }

    /// <summary>Every candidate having ended again means the game never really started.</summary>
    [Fact]
    public async Task Learn_ReturnsNothingWhenTheCandidateEndedAgain()
    {
        var processes = new FakeGameProcesses();
        var learner = new GameProcessLearner(processes, new AutoAdvanceTimeProvider(), Logger.None);
        IReadOnlySet<int> before = learner.Snapshot();
        processes.Running.Add(Process(10, "Launcher", InstallFolder + @"\Launcher.exe"));
        processes.OnListed = call =>
        {
            if (call == 3)
            {
                processes.Running.Clear();
            }
        };

        (await learner.LearnAsync(before, InstallFolder, Ct)).ShouldBeNull();
    }

    /// <summary>Without a folder – the case the install source could not name one – everything new is a candidate.</summary>
    [Fact]
    public async Task Learn_WithoutAnInstallFolder_TakesTheFirstNewProcess()
    {
        var processes = new FakeGameProcesses();
        var learner = new GameProcessLearner(processes, new AutoAdvanceTimeProvider(), Logger.None);
        IReadOnlySet<int> before = learner.Snapshot();
        processes.Running.Add(Process(7, "SomeGame", @"C:\Elsewhere\SomeGame.exe", startedSecondsIn: 2));

        (await learner.LearnAsync(before, installFolder: null, Ct))!.Name.ShouldBe("SomeGame");
    }

    [Fact]
    public void IsFromInstallFolder_MatchesOnlyBelowTheFolder()
    {
        GameProcessLearner.IsFromInstallFolder(Process(1, "g", InstallFolder + @"\Bin64\game.exe"), InstallFolder).ShouldBeTrue();
        GameProcessLearner.IsFromInstallFolder(Process(1, "g", @"C:\Other\game.exe"), InstallFolder).ShouldBeFalse();
        // A folder whose name merely starts the same must not count.
        GameProcessLearner.IsFromInstallFolder(Process(1, "g", InstallFolder + @"Backup\game.exe"), InstallFolder).ShouldBeFalse();
    }

    /// <summary>An unreadable path is the normal answer for an elevated game; dropping it would lose exactly those.</summary>
    [Fact]
    public void IsFromInstallFolder_AcceptsAnUnreadablePath()
        => GameProcessLearner.IsFromInstallFolder(Process(1, "g", null), InstallFolder).ShouldBeTrue();
}
