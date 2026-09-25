using System.IO;
using System.IO.Compression;
using RigShift.App.Services;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>One ZIP for a bug report: diagnostics, the newest logs, the data files, no user name (v4 finding E-07).</summary>
public sealed class SupportPackageTests : IDisposable
{
    private const string UserProfile = @"C:\Users\Jane Doe";

    private readonly string _directory = Directory.CreateTempSubdirectory("rigshift-support-").FullName;
    private readonly AppPaths _paths;

    public SupportPackageTests()
    {
        _paths = new AppPaths(_directory);
        Directory.CreateDirectory(_paths.Logs);
        Directory.CreateDirectory(_paths.Profiles);
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Write_HoldsDiagnosticsTheNewestThreeLogsAndTheDataFiles()
    {
        foreach (string day in (string[])["20260920", "20260921", "20260922", "20260923"])
        {
            File.WriteAllText(Path.Combine(_paths.Logs, $"rigshift-{day}.log"), day);
        }

        File.WriteAllText(_paths.SettingsFile, "{}");
        File.WriteAllText(Path.Combine(_directory, "games.json"), "[]");
        File.WriteAllText(Path.Combine(_paths.Profiles, "a.json"), "{}");

        Dictionary<string, string> entries = Write("report");

        entries.Keys.ShouldBe(
            ["diagnostics.txt", "logs/rigshift-20260921.log", "logs/rigshift-20260922.log", "logs/rigshift-20260923.log",
             "settings.json", "games.json", "profiles/a.json"],
            ignoreOrder: true);
        entries["diagnostics.txt"].ShouldBe("report");
    }

    [Fact]
    public void Write_ReplacesTheUserNameInLogsAndJson()
    {
        File.WriteAllText(Path.Combine(_paths.Logs, "rigshift-20260925.log"), @"Started C:\Users\Jane Doe\SimHub\SimHubWPF.exe");
        File.WriteAllText(Path.Combine(_paths.Profiles, "a.json"), """{ "path": "C:\\Users\\Jane Doe\\SimHub\\SimHubWPF.exe", "other": "D:\\Users\\jane\\x.exe" }""");

        Dictionary<string, string> entries = Write(string.Empty);

        entries["logs/rigshift-20260925.log"].ShouldBe(@"Started %USERPROFILE%\SimHub\SimHubWPF.exe");
        entries["profiles/a.json"].ShouldBe("""{ "path": "%USERPROFILE%\\SimHub\\SimHubWPF.exe", "other": "D:\\Users\\<user>\\x.exe" }""");
    }

    [Fact]
    public void Write_ReadsTheLogTheTrayAppIsWriting()
    {
        string log = Path.Combine(_paths.Logs, "rigshift-20260925.log");
        using var writer = new FileStream(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        writer.Write("open"u8);
        writer.Flush();

        Write(string.Empty)["logs/rigshift-20260925.log"].ShouldBe("open");
    }

    [Fact]
    public void Write_EmptyDataFolder_HoldsOnlyTheDiagnostics()
    {
        Directory.Delete(_paths.Logs);
        Directory.Delete(_paths.Profiles);

        Write("report").Keys.ShouldBe(["diagnostics.txt"]);
    }

    private Dictionary<string, string> Write(string diagnostics)
    {
        using var stream = new MemoryStream();
        int count = SupportPackage.Write(stream, diagnostics, _paths, UserProfile);
        stream.Position = 0;
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.Entries.Count.ShouldBe(count);
        return archive.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        });
    }
}
