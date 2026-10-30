using System.IO;

namespace RigShift.App.Services;

/// <summary>Where RigShift keeps its files: <c>%AppData%\RigShift</c>.</summary>
public sealed record AppPaths(string DataDirectory)
{
    /// <summary>
    /// <c>%AppData%\RigShift</c>: Velopack installs into <c>%LocalAppData%\RigShift</c> and deletes that folder on
    /// uninstall, so profiles and settings must live elsewhere.
    /// </summary>
    public static AppPaths ForCurrentUser() => new(Path.GetFullPath(
#if DEBUG
        // Developer aid: screenshots of steps that save profiles, without touching the real data.
        Environment.GetEnvironmentVariable("RIGSHIFT_DATA_DIR") is { Length: > 0 } previewData ? previewData :
#endif
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift")));

    public string Profiles => Path.GetFullPath(Path.Combine(DataDirectory, "profiles"));

    public string Logs => Path.GetFullPath(Path.Combine(DataDirectory, "logs"));

    public string SettingsFile => Path.GetFullPath(Path.Combine(DataDirectory, "settings.json"));

    /// <summary>Composed icons of game shortcuts; a shortcut points here, so they must outlive the install folder.</summary>
    public string Icons => Path.GetFullPath(Path.Combine(DataDirectory, "icons"));
}
