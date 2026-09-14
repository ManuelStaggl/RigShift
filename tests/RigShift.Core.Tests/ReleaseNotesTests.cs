using System.Text.RegularExpressions;
using RigShift.Core.Updates;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void ToPlainText_ChangelogSection_IsCompact()
    {
        const string markdown = """
            ### Added

            - Settings show the **installed version** and a `Check for updates` button.
            - A downloaded update can be installed right away; clicking the update
              notification opens the settings.

            ### Fixed

            - See [issue 12](https://github.com/ManuelStaggl/RigShift/issues/12).
            """;

        ReleaseNotes.ToPlainText(markdown).ShouldBe("""
            Added
            • Settings show the installed version and a Check for updates button.
            • A downloaded update can be installed right away; clicking the update notification opens the settings.
            Fixed
            • See issue 12.
            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Parse_RealSection1_3_1_OneLinePerBulletAndHeading()
    {
        string section = ChangelogSection("1.3.1");

        IReadOnlyList<ReleaseNoteLine> lines = ReleaseNotes.Parse(section);

        lines.Where(l => l.IsHeading).Select(l => l.Text).ShouldBe(["Added", "Fixed", "Security", "Changed"]);
        lines.Count(l => !l.IsHeading).ShouldBe(Regex.Count(section, "(?m)^- ", RegexOptions.None, TimeSpan.FromSeconds(1)));
        lines.ShouldAllBe(l => l.Text.Length > 0 && !l.Text.Contains('`') && !l.Text.Contains("**") && !l.Text.StartsWith('#'));
        lines.Where(l => !l.IsHeading).ShouldAllBe(l => l.Text.StartsWith("• "));
        lines.ShouldContain(l => l.Text.EndsWith("so it cannot create a second profile with the same name."));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ToPlainText_NoNotes_ReturnsEmpty(string? markdown) =>
        ReleaseNotes.ToPlainText(markdown).ShouldBeEmpty();

    /// <summary>The section the release workflow puts into the release notes (same pattern as release.yml).</summary>
    private static string ChangelogSection(string version)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CHANGELOG.md")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        string changelog = File.ReadAllText(Path.Combine(directory.FullName, "CHANGELOG.md")).ReplaceLineEndings("\n");
        Match section = Regex.Match(changelog, $@"(?ms)^## \[{Regex.Escape(version)}\][^\n]*\n(.*?)(?=^## \[|\z)", RegexOptions.None, TimeSpan.FromSeconds(1));
        section.Success.ShouldBeTrue();
        return section.Groups[1].Value.Trim();
    }
}
