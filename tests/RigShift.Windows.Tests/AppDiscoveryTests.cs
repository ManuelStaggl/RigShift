using RigShift.Windows.Apps;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class AppDiscoveryTests
{
    private static readonly DiscoveredApp SimHub = new("SimHub", @"C:\Program Files (x86)\SimHub\SimHubWPF.exe", false);
    private static readonly DiscoveredApp CrewChief = new("Crew Chief V4", @"C:\Program Files (x86)\Britton IT Ltd\CrewChiefV4\CrewChiefV4.exe", false);
    private static readonly DiscoveredApp Uninstall = new("Uninstall SimHub", @"C:\Program Files (x86)\SimHub\unins000.exe", false);

    [Fact]
    public void Merge_OneEntryPerExecutable_RunningFirst_KeepsStartMenuName_DropsUninstallers()
    {
        var running = new[]
        {
            new DiscoveredApp("SimHub WPF", @"c:\program files (x86)\simhub\SimHubWPF.exe", true),
            new DiscoveredApp("Discord", @"C:\Users\Test\AppData\Local\Discord\app-1.0\Discord.exe", true),
        };

        IReadOnlyList<DiscoveredApp> apps = AppDiscovery.Merge([SimHub, CrewChief, Uninstall, SimHub], running);

        apps.Select(a => (a.Name, a.IsRunning)).ShouldBe([("Discord", true), ("SimHub", true), ("Crew Chief V4", false)]);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("crew", true)]
    [InlineData("chief v4", true)]
    [InlineData("CrewChiefV4.exe", true)]
    [InlineData("crew hub", false)]
    public void Matches_EveryWordInNameOrFileName(string? query, bool expected) =>
        AppDiscovery.Matches(CrewChief, query).ShouldBe(expected);
}
