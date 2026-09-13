using RigShift.Core.Profiles;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class ProfileIconsTests
{
    [Theory]
    [InlineData("desk", "desk")]
    [InlineData("Rig", "rig")]
    [InlineData(" VR ", "vr")]
    [InlineData("tv", "tv")]
    [InlineData("STREAM", "stream")]
    public void Normalize_KnownKey_ReturnsCanonicalKey(string key, string expected) =>
        ProfileIcons.Normalize(key).ShouldBe(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("icons/custom.ico")]
    public void Normalize_UnknownOrMissingKey_ReturnsNull(string? key) =>
        ProfileIcons.Normalize(key).ShouldBeNull();

    [Fact]
    public void All_ContainsTheFiveBrandSymbols() =>
        ProfileIcons.All.ShouldBe(["desk", "rig", "vr", "tv", "stream"]);
}
