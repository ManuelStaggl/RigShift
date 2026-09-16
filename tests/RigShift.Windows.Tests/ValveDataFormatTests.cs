using RigShift.Windows.Games;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class ValveDataFormatTests
{
    [Fact]
    public void Parse_ReadsNestedBlocksAndValues()
    {
        ValveNode root = ValveDataFormat.Parse("""
            "AppState"
            {
            	"appid"		"266410"
            	"name"		"iRacing"
            	"UserConfig"
            	{
            		"language"		"english"
            	}
            }
            """);

        ValveNode state = root.Child("AppState").ShouldNotBeNull();
        state.Value("appid").ShouldBe("266410");
        state.Value("name").ShouldBe("iRacing");
        state.Child("UserConfig")!.Value("language").ShouldBe("english");
    }

    /// <summary>Windows paths arrive with doubled backslashes; a raw one would swallow the next character.</summary>
    [Fact]
    public void Parse_ResolvesEscapedBackslashesInPaths()
        => ValveDataFormat.Parse("\"libraryfolders\" { \"0\" { \"path\" \"D:\\\\SteamLibrary\" } }")
            .Child("libraryfolders")!.Child("0")!.Value("path").ShouldBe(@"D:\SteamLibrary");

    [Fact]
    public void Parse_IgnoresComments()
        => ValveDataFormat.Parse("""
            // a comment
            "AppState"
            {
            	"appid"		"1" // trailing comment
            }
            """).Child("AppState")!.Value("appid").ShouldBe("1");

    [Fact]
    public void Parse_KeysAreCaseInsensitive()
        => ValveDataFormat.Parse("\"LibraryFolders\" { \"AppID\" \"7\" }")
            .Child("libraryfolders")!.Value("appid").ShouldBe("7");

    /// <summary>A truncated or malformed file must return what it could read, not throw.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("\"AppState\" {")]
    [InlineData("}}}")]
    [InlineData("\"AppState\" { \"appid\"")]
    [InlineData("{ \"orphan\" \"block\" }")]
    public void Parse_SurvivesMalformedInput(string text)
        => Should.NotThrow(() => ValveDataFormat.Parse(text));

    [Fact]
    public void Parse_ReadsUnquotedTokens()
        => ValveDataFormat.Parse("AppState { appid 266410 }").Child("AppState")!.Value("appid").ShouldBe("266410");

    [Fact]
    public void Value_OfAnUnknownKeyIsNull()
        => ValveDataFormat.Parse("\"AppState\" { }").Child("AppState")!.Value("nope").ShouldBeNull();
}
