using System.Text.RegularExpressions;

namespace RigShift.Core.Updates;

/// <summary>One line of release notes as shown in the app: a section heading or a bullet / paragraph.</summary>
public sealed record ReleaseNoteLine(string Text, bool IsHeading);

/// <summary>Turns the Markdown release notes of an update (a CHANGELOG.md section) into compact lines for the app.</summary>
public static partial class ReleaseNotes
{
    /// <summary>
    /// Headings and bullets without blank lines between them, wrapped bullet text joined, emphasis, code marks and link
    /// targets removed. Compact on purpose: the whole section used to fill the about page (hardware test of 1.3.1).
    /// </summary>
    public static IReadOnlyList<ReleaseNoteLine> Parse(string? markdown)
    {
        var lines = new List<ReleaseNoteLine>();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return lines;
        }

        bool continuable = false;
        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continuable = false;
            }
            else if (trimmed.StartsWith('#'))
            {
                lines.Add(new ReleaseNoteLine(Inline(trimmed.TrimStart('#').TrimStart()), IsHeading: true));
                continuable = false;
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                lines.Add(new ReleaseNoteLine("• " + Inline(trimmed[2..]), IsHeading: false));
                continuable = true;
            }
            else if (continuable)
            {
                // Wrapped text continues the previous bullet or paragraph.
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + Inline(trimmed) };
            }
            else
            {
                lines.Add(new ReleaseNoteLine(Inline(trimmed), IsHeading: false));
                continuable = true;
            }
        }

        return lines;
    }

    public static string ToPlainText(string? markdown) => string.Join("\n", Parse(markdown).Select(l => l.Text));

    private static string Inline(string text) =>
        Link().Replace(text, "$1").Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]+\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Link();
}
