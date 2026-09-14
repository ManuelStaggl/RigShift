namespace RigShift.Core.Cli;

/// <summary>
/// The <c>rigshift://apply/&lt;name&gt;</c> link (docs/PLAN.md, section 6): Windows starts <c>RigShift.exe "&lt;uri&gt;"</c>,
/// which becomes <c>apply &lt;name&gt;</c> with the usual confirmation. Only <c>apply</c> exists – a link on a web page
/// must not be able to save or change profiles.
/// </summary>
public static class RigShiftUri
{
    public const string Scheme = "rigshift";

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
            || !string.Equals(parsed.Host, "apply", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(parsed.Query)
            || !string.IsNullOrEmpty(parsed.Fragment))
        {
            return null;
        }

        string name = Uri.UnescapeDataString(parsed.AbsolutePath.Trim('/')).Trim();
        return name.Length == 0 || name.Contains('/', StringComparison.Ordinal) ? null : ["apply", name];
    }
}
