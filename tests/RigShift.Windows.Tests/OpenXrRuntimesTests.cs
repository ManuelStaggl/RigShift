using RigShift.Windows.Games;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class OpenXrRuntimesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("rigshift-openxr-").FullName;

    [Fact]
    public void NameOf_TakesTheRuntimeNameFromTheManifest()
    {
        string file = Write("virtualdesktop-openxr.json", """{"file_format_version":"1.0.0","runtime":{"name":"VirtualDesktopXR","library_path":"x.dll"}}""");

        OpenXrRuntimes.NameOf(file).ShouldBe("VirtualDesktopXR");
    }

    [Theory]
    [InlineData("""{"runtime":{"library_path":"x.dll"}}""")]
    [InlineData("not json")]
    public void NameOf_WithoutAReadableName_FallsBackToTheFileName(string content)
        => OpenXrRuntimes.NameOf(Write("steamxr_win64.json", content)).ShouldBe("steamxr_win64");

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Write(string name, string content)
    {
        string file = Path.Combine(_directory, name);
        File.WriteAllText(file, content);
        return file;
    }
}
