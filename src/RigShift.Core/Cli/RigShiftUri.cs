namespace RigShift.Core.Cli;

/// <summary>
/// The <c>rigshift://apply/&lt;name&gt;</c>, <c>rigshift://toggle</c> and <c>rigshift://play/&lt;name&gt;</c> links:
/// Windows starts <c>RigShift.exe "&lt;uri&gt;"</c>, which becomes <c>apply &lt;name&gt; --from-link</c>,
/// <c>toggle --from-link</c> or <c>play &lt;name&gt; --from-link</c>. Only switching and starting a configured game
/// exist – a link on a web page must not be able to save or change profiles, and it can only reach what the user set
/// up here – and a switch always asks for confirmation, even when confirmation is turned off. For a game that means:
/// declining the switch ends the session before a program or the game is started.
/// </summary>
public static class RigShiftUri
{
    public const string Scheme = "rigshift";

    /// <summary>Hidden option marking a link; travels with the arguments over the pipe.</summary>
    public const string FromLinkOption = "--from-link";

    public static bool IsUri(string argument)
    {
        ArgumentNullException.ThrowIfNull(argument);
        return argument.StartsWith(Scheme + ":", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The command line arguments for <paramref name="uri"/>, or <c>null</c> if it is no valid RigShift link.</summary>
    public static IReadOnlyList<string>? ToArguments(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed)
            || !string.Equals(parsed.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return null;
        }

        string path = Uri.UnescapeDataString(parsed.AbsolutePath.Trim('/')).Trim();
        if (string.Equals(parsed.Host, "toggle", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length == 0 ? ["toggle", FromLinkOption] : null;
        }

        string? command = string.Equals(parsed.Host, "apply", StringComparison.OrdinalIgnoreCase) ? "apply"
            : string.Equals(parsed.Host, "play", StringComparison.OrdinalIgnoreCase) ? "play"
            : null;
        if (command is null)
        {
            return null;
        }

        // A leading '-' would arrive at the parser as an option instead of a name.
        return path.Length == 0 || path.Contains('/', StringComparison.Ordinal) || path.StartsWith('-')
            ? null
            : [command, path, FromLinkOption];
    }
}
