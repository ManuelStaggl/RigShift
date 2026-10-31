using System.IO.Compression;
using RigShift.Core.Storage;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class AutoBackupsTests : IDisposable
{
    private readonly string _directory = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "rigshift-tests", Guid.NewGuid().ToString("N")));

    [Fact]
    public void Write_KeepsTheNewestFive_AndNeverTouchesOtherFiles()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "profiles"));
        File.WriteAllText(Path.Combine(_directory, "profiles", "a.json"), "{}");
        string manual = Path.Combine(AutoBackups.Folder(_directory), "my-backup.zip");
        Directory.CreateDirectory(AutoBackups.Folder(_directory));
        File.WriteAllText(manual, "mine");
        var start = new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

        List<string> written = [.. Enumerable.Range(0, 7).Select(i => AutoBackups.Write(_directory, "delete", start.AddMinutes(i)))];

        Directory.GetFiles(AutoBackups.Folder(_directory), "auto-*.zip").Order(StringComparer.Ordinal).ShouldBe(written.Skip(2));
        File.ReadAllText(manual).ShouldBe("mine");
        using ZipArchive zip = ZipFile.OpenRead(written[^1]);
        zip.Entries.Select(e => e.FullName).ShouldBe(["profiles/a.json"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
