using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using RigShift.Windows.Games;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>
/// Steam's and Epic's detection against a fake installation in a temp folder – the real thing is not on the build
/// machine, and the shapes of these files are exactly what breaks.
/// </summary>
public sealed class GameLibraryTests : IDisposable
{
    private readonly string _root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Steam_FindsGamesAcrossEveryLibraryFolder()
    {
        string client = MakeSteamClient();
        string second = Path.Combine(_root, "D-Drive");
        MakeSteamGame(client, "266410", "iRacing", "iRacing");
        MakeSteamGame(second, "1066890", "Automobilista 2", "Automobilista 2");
        File.WriteAllText(Path.Combine(client, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
                "0"
                {
                    "path"		"{{client.Replace("\\", "\\\\")}}"
                    "label"		""
                }
                "1"
                {
                    "path"		"{{second.Replace("\\", "\\\\")}}"
                    "label"		""
                }
            }
            """);

        IReadOnlyList<InstalledGame> games = new SteamLibrary(Logger.None, () => client).Find();

        games.Select(g => g.Name).Order(StringComparer.Ordinal).ShouldBe(["Automobilista 2", "iRacing"]);
        InstalledGame iracing = games.Single(g => g.Name == "iRacing");
        iracing.Launch.Kind.ShouldBe(GameLaunchKind.Steam);
        iracing.Launch.Target.ShouldBe("266410");
        iracing.Launch.Uri.ShouldBe("steam://rungameid/266410");
        iracing.Launch.InstallFolder.ShouldBe(Path.Combine(client, "steamapps", "common", "iRacing"));
        // The game's own process is not knowable before the first start: a store URI hands us the client.
        iracing.Launch.ProcessName.ShouldBeNull();
    }

    /// <summary>Older <c>libraryfolders.vdf</c> files put the path straight on the numbered key.</summary>
    [Fact]
    public void Steam_ReadsTheOldLibraryFoldersShape()
    {
        string client = MakeSteamClient();
        string second = Path.Combine(_root, "E-Drive");
        MakeSteamGame(second, "244210", "Assetto Corsa", "assettocorsa");
        File.WriteAllText(Path.Combine(client, "steamapps", "libraryfolders.vdf"), $$"""
            "LibraryFolders"
            {
                "TimeNextStatsReport"		"0"
                "1"		"{{second.Replace("\\", "\\\\")}}"
            }
            """);

        new SteamLibrary(Logger.None, () => client).Find()
            .Single().Name.ShouldBe("Assetto Corsa");
    }

    [Fact]
    public void Steam_WithoutAnInstallation_FindsNothing()
        => new SteamLibrary(Logger.None, () => null).Find().ShouldBeEmpty();

    /// <summary>An install folder that no longer exists must not be handed on as a filter for learning.</summary>
    [Fact]
    public void Steam_LeavesTheInstallFolderNullWhenItIsGone()
    {
        string client = MakeSteamClient();
        File.WriteAllText(
            Path.Combine(client, "steamapps", "appmanifest_999.acf"),
            "\"AppState\"\n{\n\t\"appid\"\t\t\"999\"\n\t\"name\"\t\t\"Gone\"\n\t\"installdir\"\t\t\"Gone\"\n}");

        new SteamLibrary(Logger.None, () => client).Find().Single().Launch.InstallFolder.ShouldBeNull();
    }

    [Fact]
    public void Epic_ReadsTitleFolderAndExecutableFromTheManifests()
    {
        string manifests = Path.Combine(_root, "Manifests");
        string install = Path.Combine(_root, "Games", "Fortnite");
        Directory.CreateDirectory(manifests);
        Directory.CreateDirectory(install);
        File.WriteAllText(Path.Combine(manifests, "1234.item"), $$"""
            {
              "FormatVersion": 0,
              "AppName": "Fortnite",
              "DisplayName": "Fortnite",
              "InstallLocation": "{{install.Replace("\\", "\\\\")}}",
              "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteClient-Win64-Shipping.exe"
            }
            """);

        InstalledGame game = new EpicLibrary(Logger.None, manifests).Find().Single();

        game.Name.ShouldBe("Fortnite");
        game.Launch.Kind.ShouldBe(GameLaunchKind.Epic);
        game.Launch.Target.ShouldBe("Fortnite");
        game.Launch.InstallFolder.ShouldBe(install);
        // Epic names the real executable, so nothing has to be learned here.
        game.Launch.ProcessName.ShouldBe("FortniteClient-Win64-Shipping");
        game.Launch.Uri.ShouldBe("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true");
    }

    [Fact]
    public void Epic_SkipsBrokenManifestsAndKeepsTheRest()
    {
        string manifests = Path.Combine(_root, "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "broken.item"), "{ not json");
        File.WriteAllText(Path.Combine(manifests, "nameless.item"), "{ \"DisplayName\": \"No app name\" }");
        File.WriteAllText(Path.Combine(manifests, "good.item"), "{ \"AppName\": \"Ok\", \"DisplayName\": \"Ok Game\" }");

        new EpicLibrary(Logger.None, manifests).Find().Single().Name.ShouldBe("Ok Game");
    }

    [Fact]
    public void Epic_WithoutAnInstallation_FindsNothing()
        => new EpicLibrary(Logger.None, Path.Combine(_root, "nope")).Find().ShouldBeEmpty();

    [Fact]
    public void Library_MergesTheSourcesAndSortsThemByName()
    {
        string client = MakeSteamClient();
        MakeSteamGame(client, "266410", "iRacing", "iRacing");
        string manifests = Path.Combine(_root, "Manifests");
        Directory.CreateDirectory(manifests);
        File.WriteAllText(Path.Combine(manifests, "a.item"), "{ \"AppName\": \"Ams\", \"DisplayName\": \"Automobilista 2\" }");

        IReadOnlyList<InstalledGame> games = new GameLibrary(
            Logger.None, new SteamLibrary(Logger.None, () => client), new EpicLibrary(Logger.None, manifests)).Find();

        games.Select(g => g.Name).ShouldBe(["Automobilista 2", "iRacing"]);
        games.Select(g => g.Source).ShouldBe(["Epic", "Steam"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Epic_ProcessNameOfAnEmptyExecutableIsNull(string? executable)
        => EpicLibrary.ProcessNameOf(executable).ShouldBeNull();

    private string MakeSteamClient()
    {
        string client = Path.Combine(_root, "Steam");
        Directory.CreateDirectory(Path.Combine(client, "steamapps"));
        return client;
    }

    private static void MakeSteamGame(string libraryRoot, string appId, string name, string installDir)
    {
        string steamApps = Path.Combine(libraryRoot, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamApps, "common", installDir));
        File.WriteAllText(
            Path.Combine(steamApps, $"appmanifest_{appId}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"{name}\"\n\t\"installdir\"\t\t\"{installDir}\"\n}}");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
