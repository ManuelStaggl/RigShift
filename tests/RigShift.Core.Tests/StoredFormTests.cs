using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Serilog.Core;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.Core.Tests;

public sealed class StoredFormTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Profile_CopiesWithOtherListInstances_AreTheSame()
    {
        Profile rig = Rig() with { Apps = [new AppAction { Path = "SimHub.exe" }] };
        Profile copy = rig with { Displays = [.. rig.Displays], Apps = [.. rig.Apps] };

        (rig == copy).ShouldBeFalse("record equality compares the lists by reference");
        StoredForm.Same(rig, copy).ShouldBeTrue();
    }

    [Fact]
    public void Profile_AnyStoredChange_IsNotTheSame()
    {
        Profile rig = Rig();

        StoredForm.Same(rig, rig with { Name = "Other" }).ShouldBeFalse();
        StoredForm.Same(rig, rig with { Apps = [new AppAction { Path = "SimHub.exe" }] }).ShouldBeFalse();
        StoredForm.Same(rig, rig with { Displays = [rig.Displays[0] with { IsOptional = !rig.Displays[0].IsOptional }, .. rig.Displays.Skip(1)] }).ShouldBeFalse();
        StoredForm.Same(rig, null).ShouldBeFalse();
    }

    /// <summary>The editor compares what it saved with what the catalog loads back afterwards.</summary>
    [Fact]
    public async Task Profile_SavedAndLoadedBack_IsTheSame()
    {
        var store = new JsonProfileStore(_directory, Logger.None);
        Profile rig = Rig(audio: new AudioAssignment { PlaybackVolumePercent = 40 }) with
        {
            Apps = [new AppAction { Path = "SimHub.exe", WaitSeconds = 3 }],
            DesktopIcons = new DesktopIconLayout { CapturedAt = DateTimeOffset.Now, Icons = [new DesktopIcon { Item = "::{645FF040-5081-101B-9F08-00AA002F954E}", X = 10, Y = 20 }] },
        };

        await store.SaveAsync(rig, Ct);
        LoadResult loaded = await store.LoadAllAsync(Ct);

        StoredForm.Same(rig, loaded.Profiles.Single()).ShouldBeTrue();
    }

    [Fact]
    public async Task Game_SavedAndLoadedBack_IsTheSame_AndAChangeIsNot()
    {
        var store = new JsonGameStore(_directory, Logger.None);
        var game = new GameEntry
        {
            Id = Guid.NewGuid(),
            Name = "iRacing",
            Launch = new GameLaunch { Kind = GameLaunchKind.Executable, Target = @"C:\iRacing\iRacingUI.exe" },
            Apps = [new AppAction { Path = "SimHub.exe" }],
        };

        await store.SaveAsync(game, Ct);
        GameEntry loaded = (await store.LoadAllAsync(Ct)).Games.Single();

        StoredForm.Same(game, loaded).ShouldBeTrue();
        StoredForm.Same(game, loaded with { Launch = loaded.Launch with { ProcessName = "iRacingSim64DX11" } }).ShouldBeFalse();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
