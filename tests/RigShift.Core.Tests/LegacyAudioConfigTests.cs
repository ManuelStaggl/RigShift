using RigShift.Core.Legacy;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class LegacyAudioConfigTests
{
    [Fact]
    public void Parse_ReadsDevicePerProfile_CaseInsensitive()
    {
        const string Json = """
            {
              "Profiles": {
                "Desk": { "Audio": { "DeviceName": "Speakers", "DeviceId": "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000002}" } },
                "Rig":  { "Audio": { "DeviceName": "Headphones", "DeviceId": "{0.0.0.00000000}.{00000000-0000-0000-0000-000000000001}" } },
                "NoAudio": { }
              }
            }
            """;

        IReadOnlyDictionary<string, Core.Profiles.AudioEndpoint> audio = LegacyAudioConfig.Parse(Json);

        audio.Count.ShouldBe(2);
        audio["rig"].FriendlyName.ShouldBe("Headphones");
        audio["DESK"].EndpointId.ShouldEndWith("0002}");
    }

    [Fact]
    public void Parse_WithoutProfiles_ReturnsEmpty()
    {
        LegacyAudioConfig.Parse("{}").ShouldBeEmpty();
    }
}
