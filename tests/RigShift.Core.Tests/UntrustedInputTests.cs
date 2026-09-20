using System.IO.Compression;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Storage;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

/// <summary>Profiles, games and backups can come from somebody else; what they name is checked before it is used.</summary>
public sealed class UntrustedInputTests
{
    [Theory]
    [InlineData(@"C:\Tools\SimHub.exe")]
    [InlineData(@"""C:\Tools\SimHub.exe""")]
    [InlineData(@"%SystemRoot%\notepad.exe")]
    [InlineData(@"\\nas\tools\tool.exe")]
    public void ForStart_FullPath_IsAccepted(string path)
    {
        Path.IsPathFullyQualified(LaunchPath.ForStart(path)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("SimHub.exe")]
    [InlineData(@"Tools\SimHub.exe")]
    [InlineData(@"C:SimHub.exe")]
    [InlineData(@"\Tools\SimHub.exe")]
    [InlineData("powershell")]
    public void ForStart_AnythingLookedUpAlongThePath_IsRefused(string path)
    {
        Should.Throw<InvalidOperationException>(() => LaunchPath.ForStart(path)).Message.ShouldContain("full path");
    }

    [Fact]
    public void IsNetwork_TellsAShareFromALocalPath()
    {
        LaunchPath.IsNetwork(@"\\evil\share\x.exe").ShouldBeTrue();
        LaunchPath.IsNetwork(@"C:\Tools\x.exe").ShouldBeFalse();
    }

    [Theory]
    [InlineData(GameLaunchKind.Steam, "266410", true)]
    [InlineData(GameLaunchKind.Steam, "266410/../x", false)]
    [InlineData(GameLaunchKind.Steam, "", false)]
    [InlineData(GameLaunchKind.Steam, "12 34", false)]
    [InlineData(GameLaunchKind.Epic, "9773aa1aa54f4f7b80e44bef04986cea", true)]
    [InlineData(GameLaunchKind.Epic, "Fortnite_1.2-x", true)]
    [InlineData(GameLaunchKind.Epic, "x?action=uninstall&y", false)]
    [InlineData(GameLaunchKind.Epic, "a/b", false)]
    [InlineData(GameLaunchKind.Executable, @"C:\Games\any thing.exe", true)]
    public void HasValidTarget_LetsOnlyIdsIntoAStoreUri(GameLaunchKind kind, string target, bool expected)
    {
        var launch = new GameLaunch { Kind = kind, Target = target };

        launch.HasValidTarget.ShouldBe(expected);
        if (!expected)
        {
            launch.Uri.ShouldBeNull();
        }
    }

    [Fact]
    public void Text_FileBeyondTheLimit_IsNotRead()
    {
        string file = Path.Combine(Path.GetTempPath(), $"rigshift-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, new string('x', 2000));
        try
        {
            BoundedRead.Text(file, limit: 4000).Length.ShouldBe(2000);
            Should.Throw<IOException>(() => BoundedRead.Text(file, limit: 1000));
        }
        finally
        {
            File.Delete(file);
        }
    }

    /// <summary>A few kilobytes of zeros packed; the entry must not be unpacked past the limit.</summary>
    [Fact]
    public void Entry_UnpackingBeyondTheLimit_Stops()
    {
        using var zip = new MemoryStream();
        using (var write = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            using Stream entry = write.CreateEntry("profiles/big.json", CompressionLevel.Optimal).Open();
            entry.Write(new byte[64 * 1024]);
        }

        zip.Position = 0;
        using var read = new ZipArchive(zip, ZipArchiveMode.Read);

        Should.Throw<InvalidDataException>(() => BoundedRead.Entry(read.Entries[0], limit: 1024));
        using MemoryStream whole = BoundedRead.Entry(read.Entries[0], limit: 128 * 1024);
        whole.Length.ShouldBe(64 * 1024);
    }
}
