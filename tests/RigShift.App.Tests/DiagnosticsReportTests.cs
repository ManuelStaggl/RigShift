using System.IO;
using RigShift.App.Services;
using RigShift.Core.Topology;
using Shouldly;
using Xunit;
using static RigShift.Core.Tests.TestDisplays;

namespace RigShift.App.Tests;

public sealed class DiagnosticsReportTests
{
    [Fact]
    public void Build_ContainsVersionRecentSwitchesAndDisplayError()
    {
        var switched = new SwitchRecord(new DateTimeOffset(2026, 9, 14, 20, 15, 0, TimeSpan.Zero), "Rig", SwitchOutcome.Failed,
            AudioOutcome.Applied, AppsOutcome.NotConfigured, 3, TimeSpan.FromSeconds(4.2), 31, "A display did not become ready.", []);
        var input = new DiagnosticsInput("1.3.1", IsInstalled: true, Snapshot: null, DisplayError: "QueryDisplayConfig failed (5)",
            Playback: [], Recording: [], AudioError: null, Profiles: [Rig()], ActiveProfileId: null,
            DisplayNames: new Dictionary<string, string>(), History: [switched]);

        string report = DiagnosticsReport.Build(input);

        report.ShouldStartWith("RigShift 1.3.1");
        report.ShouldNotContain("development build");
        report.ShouldContain("Could not be read: QueryDisplayConfig failed (5)");
        report.ShouldContain("Rig: Failed, 3 attempt(s), 4.2 s");
        report.ShouldContain("error 31");
        report.ShouldContain("A display did not become ready.");
        report.ShouldContain("- Rig: Ultrawide 49 (primary)");
    }

    [Fact]
    public void Build_ReplacesTheUserNameInPaths()
    {
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var switched = new SwitchRecord(DateTimeOffset.UnixEpoch, "Rig", SwitchOutcome.Applied, AudioOutcome.NotConfigured,
            AppsOutcome.NotConfigured, 1, TimeSpan.FromSeconds(1), null, $"Could not start {Path.Combine(profile, "SimHub", "SimHubWPF.exe")}", []);
        var input = new DiagnosticsInput("1.3.1", IsInstalled: true, Snapshot: null, DisplayError: null,
            Playback: [], Recording: [], AudioError: null, Profiles: [], ActiveProfileId: null,
            DisplayNames: new Dictionary<string, string>(), History: [switched]);

        string report = DiagnosticsReport.Build(input);

        report.ShouldNotContain(profile, Case.Insensitive);
        report.ShouldContain(@"%USERPROFILE%\SimHub\SimHubWPF.exe");
    }

    [Theory]
    [InlineData(@"D:\Users\manue\Games\x.exe", @"D:\Users\<user>\Games\x.exe")]
    [InlineData(@"c:\users\Someone Else", @"c:\users\<user> Else")]
    [InlineData(@"C:\Program Files\SimHub", @"C:\Program Files\SimHub")]
    public void Anonymize_ReplacesUserFolderSegments(string text, string expected) =>
        DiagnosticsReport.Anonymize(text, userProfile: null).ShouldBe(expected);
}
