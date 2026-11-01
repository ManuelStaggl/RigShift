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
