namespace RigShift.Core.Profiles;

/// <summary>
/// The path of a program a profile or a game starts. RigShift starts programs by their full path only: a bare
/// <c>tool.exe</c> would be looked up in the current folder and along <c>PATH</c>, where a different file of that
/// name can be waiting. Stopping a program by its bare name stays possible – that starts nothing.
/// </summary>
public static class LaunchPath
{
    /// <summary>Quotes removed and <c>%variables%</c> resolved.</summary>
    public static string Expand(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
    }

    public static bool IsFullyQualified(string path) => Path.IsPathFullyQualified(Expand(path));

    /// <summary>A network share: worth a second look when the entry came from somebody else's backup.</summary>
    public static bool IsNetwork(string path) => Expand(path).StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>The expanded path, or <see cref="InvalidOperationException"/> when it is not a full path.</summary>
    public static string ForStart(string path)
    {
        string file = Expand(path);
        return Path.IsPathFullyQualified(file)
            ? file
            : throw new InvalidOperationException(
                $"'{file}' is not a full path. RigShift starts programs only by their full path, such as C:\\Tools\\{Path.GetFileName(file)}.");
    }
}
