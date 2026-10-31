using System.Text;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class GameSettingsIniTests : IDisposable
{
    // Built like Assetto Corsa's cfg\video.ini: CRLF, upper-case sections, blanks around nothing.
    private const string AssettoCorsa =
        "[VIDEO]\r\n" +
        "FULLSCREEN=1\r\n" +
        "WIDTH=7680\r\n" +
        "HEIGHT=1440\r\n" +
        "\r\n" +
        "[CAMERA]\r\n" +
        "MODE=TRIPLE\r\n" +
        "; MODE=OCULUS for VR\r\n";

    // Built like an Unreal Engine GameUserSettings.ini (Automobilista 2, rFactor-style tools): LF, a section name with
    // slashes, a key before any section, a duplicate key and a value with a semicolon.
    private const string Unreal =
        "Version=5\n" +
        "[/Script/Engine.GameUserSettings]\n" +
        "bUseVSync = False\n" +
        "ResolutionSizeX=3840\n" +
        "ResolutionSizeX=1920\n" +
        "LastUserConfirmedResolutionSizeX=3840\n" +
        "Paths=C:\\Games;D:\\Games\n" +
        "\n" +
        "[ScalabilityGroups]\n" +
        "sg.ShadowQuality=3";

    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Read_FindsSectionKeys_FirstOneWins_CaseInsensitive()
    {
        IReadOnlyDictionary<string, string> values = GameSettingsIni.Read(Bytes(Unreal), [
            "Version", "/Script/Engine.GameUserSettings/bUseVSync", "/Script/Engine.GameUserSettings/ResolutionSizeX",
            "/script/engine.gameusersettings/paths", "ScalabilityGroups/sg.ShadowQuality", "ScalabilityGroups/Missing"]);

        values.ShouldBe(new Dictionary<string, string>
        {
            ["Version"] = "5",
            ["/Script/Engine.GameUserSettings/bUseVSync"] = "False",
            ["/Script/Engine.GameUserSettings/ResolutionSizeX"] = "3840",
            ["/script/engine.gameusersettings/paths"] = @"C:\Games;D:\Games",
            ["ScalabilityGroups/sg.ShadowQuality"] = "3",
        });
    }

    [Fact]
    public void Replace_ChangesOnlyTheValues_ByteForByte()
    {
        byte[] result = GameSettingsIni.Replace(Bytes(AssettoCorsa), new Dictionary<string, string>
        {
            ["VIDEO/WIDTH"] = "2560",
            ["CAMERA/MODE"] = "OCULUS",
            ["CAMERA/Missing"] = "1",
        });

        Encoding.UTF8.GetString(result).ShouldBe(AssettoCorsa.Replace("WIDTH=7680", "WIDTH=2560").Replace("MODE=TRIPLE", "MODE=OCULUS"));
    }

    [Fact]
    public void Replace_KeepsBlanksAroundTheValue_LaterDuplicates_AndTheLastLineWithoutEnd()
    {
        byte[] result = GameSettingsIni.Replace(Bytes(Unreal), new Dictionary<string, string>
        {
            ["/Script/Engine.GameUserSettings/bUseVSync"] = "True",
            ["/Script/Engine.GameUserSettings/ResolutionSizeX"] = "2560",
            ["ScalabilityGroups/sg.ShadowQuality"] = "0",
        });

        Encoding.UTF8.GetString(result).ShouldBe(Unreal
            .Replace("bUseVSync = False", "bUseVSync = True")
            .Replace("\nResolutionSizeX=3840\n", "\nResolutionSizeX=2560\n")
            .Replace("sg.ShadowQuality=3", "sg.ShadowQuality=0"));
    }

    [Fact]
    public void Replace_RefusesValuesOverSeveralLines_AndUtf16Files()
    {
        Should.Throw<ArgumentException>(() => GameSettingsIni.Replace(Bytes(AssettoCorsa), new Dictionary<string, string> { ["VIDEO/WIDTH"] = "1\r\n[X]" }));
        byte[] utf16 = [.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(AssettoCorsa)];
        Should.Throw<InvalidDataException>(() => GameSettingsIni.Read(utf16, ["VIDEO/WIDTH"]));
    }

    [Fact]
    public void Read_SkipsTheUtf8ByteOrderMark()
    {
        byte[] withBom = [.. Encoding.UTF8.GetPreamble(), .. Bytes(AssettoCorsa)];
        GameSettingsIni.Read(withBom, ["VIDEO/FULLSCREEN"])["VIDEO/FULLSCREEN"].ShouldBe("1");
    }

    [Fact]
    public async Task Swap_WritesAnIniFile_AndPutsItBack()
    {
        string file = Path.Combine(_directory, "cfg", "video.ini");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, Bytes(AssettoCorsa), TestContext.Current.CancellationToken);
        var files = new GameSettingsFiles(Path.Combine(_directory, "backups"), new AutoAdvanceTimeProvider(), Logger.None);
        string[] keys = ["VIDEO/WIDTH", "CAMERA/MODE"];

        IReadOnlyDictionary<string, string> before = await files.SwapAsync(
            file, keys, new Dictionary<string, string> { ["VIDEO/WIDTH"] = "2560", ["CAMERA/MODE"] = "OCULUS" }, TestContext.Current.CancellationToken);
        await files.SwapAsync(file, keys, before, TestContext.Current.CancellationToken);

        before.ShouldBe(new Dictionary<string, string> { ["VIDEO/WIDTH"] = "7680", ["CAMERA/MODE"] = "TRIPLE" });
        (await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken)).ShouldBe(AssettoCorsa);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
