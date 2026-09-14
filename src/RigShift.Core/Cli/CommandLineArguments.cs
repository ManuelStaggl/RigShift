using System.Text;

namespace RigShift.Core.Cli;

/// <summary>Builds command-line strings that Windows splits back into the same arguments (shortcuts, analysis finding G-04).</summary>
public static class CommandLineArguments
{
    /// <summary>
    /// Quotes one argument by the <c>CommandLineToArgvW</c> rules: backslashes are literal unless they precede a quote,
    /// so backslashes before an escaped quote and before the closing quote are doubled.
    /// </summary>
    public static string Quote(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);

        var text = new StringBuilder(argument.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                text.Append('\\', (backslashes * 2) + 1);
            }
            else
            {
                text.Append('\\', backslashes);
            }

            text.Append(c);
            backslashes = 0;
        }

        return text.Append('\\', backslashes * 2).Append('"').ToString();
    }
}
