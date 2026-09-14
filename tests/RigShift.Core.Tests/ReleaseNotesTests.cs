using RigShift.Core.Updates;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class ReleaseNotesTests
{
    [Fact]
    public void ToPlainText_ChangelogSection_ReadsAsText()
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \n ")]
    public void ToPlainText_NoNotes_ReturnsEmpty(string? markdown) =>
        ReleaseNotes.ToPlainText(markdown).ShouldBeEmpty();
}
