using Microsoft.Extensions.DependencyInjection;
using RigShift.App.ViewModels;
using Shouldly;
using Xunit;

namespace RigShift.App.Tests;

/// <summary>The help page's part in a bug report: what the diagnostics carry, and where the form opens.</summary>
public sealed class AboutViewModelTests : IDisposable
{
    private readonly DemoServices _demo = new();

    public void Dispose() => _demo.Dispose();

    [Fact]
    public async Task Report_CarriesDisplaysProfilesSettingsAndGames()
    {
        using var ui = new DispatcherThread();
        await ui.RunAsync(async () =>
        {
            using ServiceProvider services = await _demo.StartAsync();
            AboutViewModel about = services.GetRequiredService<AboutViewModel>();

            string report = await about.BuildReportAsync();

            report.ShouldContain("## Graphics");
            report.ShouldContain("## Displays");
            report.ShouldContain("Desk 4K: active");
            report.ShouldContain("- Desk [active]");
            report.ShouldContain("- Rig:");
            report.ShouldContain("## Settings");
            report.ShouldContain("Games: 1");
        });
    }

    [Fact]
    public void IssueUrl_OpensTheBugFormWithTheVersion() =>
        AboutViewModel.IssueUrl("4.0.0+abc").ShouldBe(
            "https://github.com/ManuelStaggl/RigShift/issues/new?template=bug_report.yml&version=4.0.0%2Babc");
}
