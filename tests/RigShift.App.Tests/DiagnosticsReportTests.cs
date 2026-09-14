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
}
