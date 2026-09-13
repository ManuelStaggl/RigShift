using RigShift.Core.Legacy;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class LegacyDisplayFileTests
{
    private static readonly string PathBlob = Convert.ToBase64String(new byte[72]);
    private static readonly string ModeBlob = Convert.ToBase64String(new byte[64]);

    [Fact]
    public void Parse_ReadsPathsAndModes()
    {
        string content =
            $"P|{PathBlob}|\\\\?\\PCI#VEN_10DE|\\\\?\\PCI#VEN_10DE|\\\\?\\DISPLAY#SAM749B#1|Odyssey G93SC\r\n" +
            $"M|{ModeBlob}|\\\\?\\PCI#VEN_10DE|\r\n" +
            $"M|{ModeBlob}|\\\\?\\PCI#VEN_10DE|\\\\?\\DISPLAY#SAM749B#1\r\n";

        LegacyDisplayProfile profile = LegacyDisplayFile.Parse(content);

        profile.Paths.Count.ShouldBe(1);
        profile.Paths[0].FriendlyName.ShouldBe("Odyssey G93SC");
        profile.Paths[0].TargetDevicePath.ShouldBe("\\\\?\\DISPLAY#SAM749B#1");
        profile.Paths[0].PathStruct.Length.ShouldBe(72);
        profile.Modes.Count.ShouldBe(2);
        profile.Modes[1].TargetDevicePath.ShouldBe("\\\\?\\DISPLAY#SAM749B#1");
    }

    [Fact]
    public void Parse_AllowsEmptyFriendlyName_ForVirtualDisplays()
    {
        string content = $"P|{PathBlob}|\\\\?\\SWD#spacedesk|\\\\?\\SWD#spacedesk|\\\\?\\DISPLAY#Default_Monitor#1|\r\n";

        LegacyDisplayProfile profile = LegacyDisplayFile.Parse(content);

        profile.Paths[0].FriendlyName.ShouldBeEmpty();
    }

    [Fact]
    public void Parse_WithoutPaths_Throws()
    {
        Should.Throw<FormatException>(() => LegacyDisplayFile.Parse($"M|{ModeBlob}|adapter|\r\n"));
    }

    [Fact]
    public void Parse_UnknownLine_Throws()
    {
        Should.Throw<FormatException>(() => LegacyDisplayFile.Parse("X|garbage"));
    }
}
