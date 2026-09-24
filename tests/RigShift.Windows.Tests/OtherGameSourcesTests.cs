using RigShift.Core.Games;
using RigShift.Windows.Games;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>Games outside Steam and Epic: iRacing's own installer, the EA app, the Xbox app (v4 finding U-07).</summary>
public sealed class OtherGameSourcesTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("rigshift-games-").FullName;

    [Fact]
    public void PublisherId_OfMicrosoft_IsTheWellKnownOne() =>
        XboxLibrary.PublisherId("CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US").ShouldBe("8wekyb3d8bbwe");

    [Fact]
    public void Xbox_ConfigWithIdentityAndPcExecutable_StartsThroughAppsFolder()
    {
        const string Config = """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1">
              <Identity Name="Microsoft.ForzaMotorsport" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" Version="1.0.0.0" />
              <ExecutableList>
                <Executable Name="forza_x64_release_final.exe" TargetDeviceFamily="PC" Id="Game" />
              </ExecutableList>
              <ShellVisuals DefaultDisplayName="Forza Motorsport" />
            </Game>
            """;

        var game = XboxLibrary.Parse(Config, @"D:\XboxGames\Forza Motorsport\Content", "Forza Motorsport").ShouldNotBeNull();

        game.Name.ShouldBe("Forza Motorsport");
        game.Source.ShouldBe("Xbox");
        game.Launch.Kind.ShouldBe(GameLaunchKind.Xbox);
        game.Launch.Target.ShouldBe("Microsoft.ForzaMotorsport_8wekyb3d8bbwe!Game");
        game.Launch.Uri.ShouldBe(@"shell:AppsFolder\Microsoft.ForzaMotorsport_8wekyb3d8bbwe!Game");
        game.Launch.ProcessName.ShouldBe("forza_x64_release_final");
    }

    [Fact]
    public void Xbox_LaunchHelperAndResourceName_LearnsTheProcessAndUsesTheFolder()
    {
        const string Config = """
            <Game configVersion="1">
              <Identity Name="Studio.Rally" Publisher="CN=Studio" Version="1.0.0.0" />
              <ExecutableList><Executable Name="GameLaunchHelper.exe" Id="Game" /></ExecutableList>
              <ShellVisuals DefaultDisplayName="ms-resource:AppDisplayName" />
            </Game>
            """;

        var game = XboxLibrary.Parse(Config, @"C:\XboxGames\Rally\Content", "Rally").ShouldNotBeNull();

        game.Name.ShouldBe("Rally");
        game.Launch.ProcessName.ShouldBeNull("a launch helper is not the game; the first start learns the real one");
    }

    [Fact]
    public void Uninstall_IRacingEntry_StartsTheInterfaceAndIsNamedForTheTemplate()
    {
        string ui = Path.Combine(_folder, "iRacing", "ui");
        Directory.CreateDirectory(ui);
        File.WriteAllBytes(Path.Combine(ui, "iRacingUI.exe"), []);

        var game = UninstallLibrary.ToGame(new UninstallEntry("iRacing.com Motorsport Simulator", "iRacing.com", Path.Combine(_folder, "iRacing"), null))
            .ShouldNotBeNull();

        game.Name.ShouldBe("iRacing");
        game.Source.ShouldBe("iRacing");
        game.Launch.Target.ShouldBe(Path.Combine(ui, "iRacingUI.exe"));
        SimTemplates.For(game.Launch, game.Name).ShouldNotBeNull().LauncherProcess.ShouldBe("iRacingUI");
    }

    [Fact]
    public void Uninstall_EaGame_UsesItsIconExecutable_ButNotTheEaAppOrAnUninstaller()
    {
        string exe = Path.Combine(_folder, "F1_24.exe");
        File.WriteAllBytes(exe, []);
        string uninstaller = Path.Combine(_folder, "unins000.exe");
        File.WriteAllBytes(uninstaller, []);

        var game = UninstallLibrary.ToGame(new UninstallEntry("F1® 24", "Electronic Arts", _folder, $"\"{exe}\",0")).ShouldNotBeNull();
        game.Name.ShouldBe("F1 24");
        game.Source.ShouldBe("EA");
        game.Launch.Target.ShouldBe(exe);

        UninstallLibrary.ToGame(new UninstallEntry("EA app", "Electronic Arts", _folder, exe)).ShouldBeNull();
        UninstallLibrary.ToGame(new UninstallEntry("GRID Legends", "Electronic Arts", _folder, uninstaller)).ShouldBeNull();
        UninstallLibrary.ToGame(new UninstallEntry("Some Tool", "Someone", _folder, exe)).ShouldBeNull();
    }

    [Fact]
    public void Uninstall_NoEntry_FindsIRacingInItsDefaultFolder()
    {
        string folder = Path.Combine(_folder, "iRacing");
        Directory.CreateDirectory(Path.Combine(folder, "ui"));
        File.WriteAllBytes(Path.Combine(folder, "ui", "iRacingUI.exe"), []);

        var library = new UninstallLibrary(Logger.None, () => [], folder);

        library.Find().ShouldHaveSingleItem().Name.ShouldBe("iRacing");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
