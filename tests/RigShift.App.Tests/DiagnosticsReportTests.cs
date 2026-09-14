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

    [Fact]
    public void Build_ErrorCodeOnlyForFailedSwitches()
    {
        // HW-17: a transient 1610 on the way to a successful switch looked like the cause.
        var switched = new SwitchRecord(DateTimeOffset.UnixEpoch, "Rig", SwitchOutcome.Applied, AudioOutcome.NotConfigured,
            AppsOutcome.NotConfigured, 6, TimeSpan.FromSeconds(7), 1610, null, ["spacedesk"]);
        var input = new DiagnosticsInput("1.4.1", IsInstalled: true, Snapshot: null, DisplayError: null,
            Playback: [], Recording: [], AudioError: null, Profiles: [], ActiveProfileId: null,
            DisplayNames: new Dictionary<string, string>(), History: [switched]);

        DiagnosticsReport.Build(input).ShouldNotContain("error 1610");
    }

    [Theory]
    [InlineData(@"\\?\DISPLAY#AUS32F6#5&2a2a2a2a&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", "AUS32F6 · UID4353")]
    [InlineData(@"\\?\DISPLAY#Default_Monitor#TEST&5", "Default_Monitor")]
    [InlineData("", "(unrecognized)")]
    public void ShortTargetPath_KeepsModelAndUid(string path, string expected) =>
        DiagnosticsReport.ShortTargetPath(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"\\?\PCI#VEN_10DE&DEV_2702&SUBSYS_51841458&REV_A1#4&1a2b3c4d&0&0019#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", "VEN_10DE&DEV_2702")]
    [InlineData(@"\\?\ROOT#DISPLAY#0000#{5b45201d-f2f2-4f3b-85bb-30ff1f953599}", @"ROOT\DISPLAY")]
    public void ShortAdapterPath_KeepsVendorAndDevice(string path, string expected) =>
        DiagnosticsReport.ShortAdapterPath(path).ShouldBe(expected);

    [Theory]
    [InlineData(@"D:\Users\manue\Games\x.exe", @"D:\Users\<user>\Games\x.exe")]
    [InlineData(@"c:\users\Someone Else", @"c:\users\<user> Else")]
    [InlineData(@"C:\Program Files\SimHub", @"C:\Program Files\SimHub")]
    public void Anonymize_ReplacesUserFolderSegments(string text, string expected) =>
        DiagnosticsReport.Anonymize(text, userProfile: null).ShouldBe(expected);
}
