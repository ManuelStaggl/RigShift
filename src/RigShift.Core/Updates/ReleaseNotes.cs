using System.Text.RegularExpressions;

namespace RigShift.Core.Updates;

/// <summary>Turns the Markdown release notes of an update (a CHANGELOG.md section) into plain text for the settings.</summary>
public static partial class ReleaseNotes
{
    public static string ToPlainText(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var lines = new List<string>();
        bool continuable = false;
        foreach (string raw in markdown.ReplaceLineEndings("\n").Split('\n'))
        {
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                {
                    lines.Add(string.Empty);
                }

                continuable = false;
                continue;
            }

            string text = Inline(trimmed.TrimStart('#').TrimStart());
            if (trimmed.StartsWith('#'))
            {
                lines.Add(text);
                continuable = false;
            }
            else if (trimmed.StartsWith("- ", StringComparison.Ordinal) || trimmed.StartsWith("* ", StringComparison.Ordinal))
            {
                lines.Add("• " + Inline(trimmed[2..]));
                continuable = true;
            }
            else if (continuable && char.IsWhiteSpace(raw[0]))
            {
                // Wrapped bullet text continues the previous bullet.
                lines[^1] += " " + text;
            }
            else
            {
                lines.Add(text);
                continuable = true;
            }
        }

        return string.Join("\n", lines).Trim();
    }

    private static string Inline(string text) =>
        Link().Replace(text, "$1").Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

    [GeneratedRegex(@"\[([^\]]+)\]\([^)\s]+\)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Link();
}
