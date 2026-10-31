using RigShift.Core.Games;
using RigShift.Windows.Games;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>
/// Finding the executable of a configured game – the icon source of a game's desktop shortcut. Against a fake install
/// folder in a temp directory, so it does not depend on what is installed on the machine running the tests.
/// </summary>
public sealed class GameExecutableTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Find_ExecutableLaunch_TakesThePathItself()
    {
        string file = Make("Game", "Game.exe");

        GameExecutable.Find(new GameLaunch { Kind = GameLaunchKind.Executable, Target = file }, Logger.None).ShouldBe(file);
    }

    [Fact]
    public void Find_ExecutableLaunchThatIsGone_FindsNothing()
    {
        GameExecutable.Find(
            new GameLaunch { Kind = GameLaunchKind.Executable, Target = Path.Combine(_root, "nope.exe") },
            Logger.None).ShouldBeNull();
    }

    [Fact]
    public void Find_StoreLaunch_LooksForTheProcessNameBelowTheInstallFolder()
    {
        string folder = Path.Combine(_root, "iRacing");
        Make("iRacing", "unins000.exe");
        string game = Make(Path.Combine("iRacing", "Binaries", "Win64"), "iRacingSim64DX11.exe");

        string? found = GameExecutable.Find(
            new GameLaunch
            {
                Kind = GameLaunchKind.Steam,
                Target = "266410",
                InstallFolder = folder,
                ProcessName = "iRacingSim64DX11",
            },
            Logger.None);

        found.ShouldBe(game);
    }

    [Fact]
    public void Find_StoreLaunchWithoutAKnownProcessName_FindsNothing()
    {
        string folder = Path.Combine(_root, "iRacing");
        Make("iRacing", "iRacing.exe");

        GameExecutable.Find(
            new GameLaunch { Kind = GameLaunchKind.Steam, Target = "266410", InstallFolder = folder },
            Logger.None).ShouldBeNull();
    }

    [Fact]
    public void Find_InstallFolderThatIsGone_FindsNothing()
    {
        GameExecutable.Find(
            new GameLaunch
            {
                Kind = GameLaunchKind.Epic,
                Target = "Fortnite",
                InstallFolder = Path.Combine(_root, "gone"),
                ProcessName = "Fortnite",
            },
            Logger.None).ShouldBeNull();
    }

    [Fact]
    public void GuessProcessName_TakesTheLargestProgram_ThatIsNoHelper()
    {
        // Built like an Unreal game on Steam: a small starter on top, the real game deep down, helpers around it.
        string folder = Path.Combine(_root, "Rally");
        Make("Rally", "Rally.exe", 200);
        Make(Path.Combine("Rally", "Rally", "Binaries", "Win64"), "Rally-Win64-Shipping.exe", 5000);
        Make(Path.Combine("Rally", "_CommonRedist", "vcredist"), "VC_redist.x64.exe", 9000);
        Make(Path.Combine("Rally", "EasyAntiCheat"), "EasyAntiCheat_EOS_Setup.exe", 9000);
        Make("Rally", "UnityCrashHandler64.exe", 9000);
        Make("Rally", "RallyLauncher.exe", 9000);

        GameExecutable.GuessProcessName(folder, Logger.None).ShouldBe("Rally-Win64-Shipping");
        GameExecutable.GuessProcessName(Path.Combine(_root, "missing"), Logger.None).ShouldBeNull();
    }

    [Fact]
    public void WithGuessedProcess_LeavesKnownNamesAndExecutablesAlone()
    {
        string folder = Path.Combine(_root, "Game");
        Make("Game", "Game.exe", 100);
        var steam = new GameLaunch { Kind = GameLaunchKind.Steam, Target = "1", InstallFolder = folder };

        GameLaunch guessed = GameLibrary.WithGuessedProcess(steam, Logger.None);
        guessed.ProcessName.ShouldBe("Game");
        guessed.ProcessNameGuessed.ShouldBeTrue();

        GameLibrary.WithGuessedProcess(steam with { ProcessName = "Learned" }, Logger.None).ShouldBe(steam with { ProcessName = "Learned" });
        var exe = new GameLaunch { Kind = GameLaunchKind.Executable, Target = Path.Combine(folder, "Game.exe"), InstallFolder = folder };
        GameLibrary.WithGuessedProcess(exe, Logger.None).ShouldBe(exe);
    }

    private string Make(string relativeFolder, string file, int bytes)
    {
        string path = Make(relativeFolder, file);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private string Make(string relativeFolder, string file)
    {
        string folder = Path.Combine(_root, relativeFolder);
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, file);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
