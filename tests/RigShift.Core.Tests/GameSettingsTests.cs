using System.Text;
using RigShift.Core.Games;
using RigShift.Core.Storage;
using RigShift.Core.Tests.Fakes;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class GameSettingsTests : IDisposable
{
    // Built like LMU's Settings.JSON: CRLF, comment keys next to the values, numbers as the game writes them.
    private const string Lmu =
        "{\r\n" +
        "  \"Graphic Options\": {\r\n" +
        "    \"Max Visible Vehicles\": 25,\r\n" +
        "    \"Shadows\": 4,\r\n" +
        "    \"Shadows#\": \"0=Off 1=Low 2=Medium 3=High 4=Ultra\",\r\n" +
        "    \"Garage Car Detail\": 0.10000000149011612,\r\n" +
        "    \"Rain\": { \"Shadows\": 9 },\r\n" +
        "    \"List\": [ { \"Shadows\": 8 }, 7 ]\r\n" +
        "  },\r\n" +
        "  \"Sound Options\": {\r\n" +
        "    \"Maximum Effects\": 255,\r\n" +
        "    \"Name\": \"Ab\\\"c\"\r\n" +
        "  }\r\n" +
        "}";

    private static readonly string[] Keys =
    [
        "Graphic Options/Max Visible Vehicles",
        "Graphic Options/Shadows",
        "Graphic Options/Garage Car Detail",
        "Graphic Options/Rain/Shadows",
        "Sound Options/Maximum Effects",
        "Sound Options/Name",
        "Graphic Options/Missing",
    ];

    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void Read_FindsValuesByTheirObjectPath_AsTheGameWroteThem()
    {
        IReadOnlyDictionary<string, string> values = GameSettingsJson.Read(Bytes(Lmu), Keys);

        values["Graphic Options/Max Visible Vehicles"].ShouldBe("25");
        values["Graphic Options/Shadows"].ShouldBe("4");
        values["Graphic Options/Garage Car Detail"].ShouldBe("0.10000000149011612");
        values["Graphic Options/Rain/Shadows"].ShouldBe("9");
        values["Sound Options/Name"].ShouldBe("\"Ab\\\"c\"");
        values.ShouldNotContainKey("Graphic Options/Missing");
        values.Count.ShouldBe(6);
    }

    [Fact]
    public void Replace_ChangesOnlyTheValues_AndLeavesEveryOtherByteAsItWas()
    {
        var values = new Dictionary<string, string>
        {
            ["Graphic Options/Shadows"] = "2",
            ["Graphic Options/Garage Car Detail"] = "0.009999999776482582",
            ["Sound Options/Name"] = "\"Z\"",
            ["Graphic Options/Missing"] = "1",
        };

        string result = Encoding.UTF8.GetString(GameSettingsJson.Replace(Bytes(Lmu), values));

        result.ShouldBe(Lmu
            .Replace("\"Shadows\": 4,", "\"Shadows\": 2,", StringComparison.Ordinal)
            .Replace("0.10000000149011612", "0.009999999776482582", StringComparison.Ordinal)
            .Replace("\"Ab\\\"c\"", "\"Z\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("25", true)]
    [InlineData("\"text\"", true)]
    [InlineData("true", true)]
    [InlineData("{ \"a\": 1 }", false)]
    [InlineData("1, \"b\": 2", false)]
    [InlineData("", false)]
    public void IsSingleValue_AcceptsOnlyOneValue(string raw, bool expected) =>
        GameSettingsJson.IsSingleValue(raw).ShouldBe(expected);

    [Fact]
    public void Replace_RefusesAValueThatWouldBreakTheFile() =>
        Should.Throw<ArgumentException>(() => GameSettingsJson.Replace(
            Bytes(Lmu), new Dictionary<string, string> { ["Graphic Options/Shadows"] = "1, \"Injected\": 2" }));

    [Theory]
    [InlineData(@"D:\Steam\Le Mans Ultimate\UserData\player\Settings.JSON", true)]
    [InlineData(@"UserData\player\Settings.JSON", false)]
    [InlineData(@"C:\Windows\win.ini", false)]
    [InlineData("", false)]
    public void HasValidFile_WantsAFullPathToAJsonFile(string file, bool expected) =>
        new GameSettings { File = file }.HasValidFile.ShouldBe(expected);

    [Fact]
    public async Task Swap_BacksUpWritesAndReturnsTheValuesFromBefore()
    {
        string file = Path.Combine(_directory, "game", "Settings.JSON");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file, Bytes(Lmu), Ct);
        string backups = Path.Combine(_directory, "backups");
        var files = new GameSettingsFiles(backups, new AutoAdvanceTimeProvider(), Logger.None);

        IReadOnlyDictionary<string, string> before = await files.SwapAsync(
            file, Keys, new Dictionary<string, string> { ["Graphic Options/Shadows"] = "2" }, Ct);

        before["Graphic Options/Shadows"].ShouldBe("4");
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(file, Ct))
            .ShouldBe(Lmu.Replace("\"Shadows\": 4,", "\"Shadows\": 2,", StringComparison.Ordinal));
        Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Directory.GetFiles(backups).ShouldHaveSingleItem(), Ct)).ShouldBe(Lmu);
        Directory.GetFiles(Path.GetDirectoryName(file)!).ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Swap_WithNothingToChange_WritesNothingAndKeepsNoBackup()
    {
        string file = Path.Combine(_directory, "Settings.JSON");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(file, Bytes(Lmu), Ct);
        DateTime written = File.GetLastWriteTimeUtc(file);
        string backups = Path.Combine(_directory, "backups");

        await new GameSettingsFiles(backups, new AutoAdvanceTimeProvider(), Logger.None).SwapAsync(
            file, Keys, new Dictionary<string, string> { ["Graphic Options/Shadows"] = "4" }, Ct);

        File.GetLastWriteTimeUtc(file).ShouldBe(written);
        Directory.Exists(backups).ShouldBeFalse();
    }

    [Fact]
    public async Task Swap_KeepsOnlyTheNewestBackups()
    {
        string file = Path.Combine(_directory, "Settings.JSON");
        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(file, Bytes(Lmu), Ct);
        string backups = Path.Combine(_directory, "backups");
        var time = new AutoAdvanceTimeProvider();
        var files = new GameSettingsFiles(backups, time, Logger.None);

        for (int i = 0; i < GameSettingsFiles.BackupsKept + 3; i++)
        {
            await files.SwapAsync(file, Keys, new Dictionary<string, string> { ["Graphic Options/Shadows"] = (i % 2).ToString(System.Globalization.CultureInfo.InvariantCulture) }, Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), time, Ct);
        }

        Directory.GetFiles(backups).Length.ShouldBe(GameSettingsFiles.BackupsKept);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
