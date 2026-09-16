using RigShift.Windows.Games;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>The shell's <c>path,index</c> notation, as Windows' uninstall entries and shortcuts write it.</summary>
public sealed class IconLocationTests
{
    [Theory]
    [InlineData("\"C:\\Games\\game.exe\",0", "C:\\Games\\game.exe", 0)]
    [InlineData("C:\\Games\\game.exe,2", "C:\\Games\\game.exe", 2)]
    [InlineData("C:\\Games\\game.exe", "C:\\Games\\game.exe", 0)]
    [InlineData("  \"C:\\Games\\game.ico\" , 1 ", "C:\\Games\\game.ico", 1)]
    [InlineData("C:\\Games\\game.exe,-1", "C:\\Games\\game.exe", -1)]
    public void Parse_SplitsPathAndIndex(string value, string path, int index)
    {
        IconLocation.Parse(value).ShouldBe((path, index));
    }

    /// <summary>A comma inside a folder name is part of the path, not an index.</summary>
    [Fact]
    public void Parse_CommaInTheFolderName_StaysInThePath()
    {
        IconLocation.Parse(@"C:\Games\Need for Speed, The Run\game.exe")
            .ShouldBe((@"C:\Games\Need for Speed, The Run\game.exe", 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",0")]
    public void Parse_WithoutAPath_IsNothing(string? value)
    {
        IconLocation.Parse(value).ShouldBeNull();
    }
}
