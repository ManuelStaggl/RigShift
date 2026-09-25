using System.IO;
using System.IO.Compression;
using System.Text;
using RigShift.Core.Storage;

namespace RigShift.App.Services;

/// <summary>
/// Everything a bug report needs in one ZIP (v4 finding E-07): <c>diagnostics.txt</c>, the three newest logs, the
/// settings, the profiles and the games. The user name is replaced everywhere, device names and paths stay – they are
/// what the report is for.
/// </summary>
public static class SupportPackage
{
    public const string DiagnosticsEntry = "diagnostics.txt";

    private const int Logs = 3;

    /// <returns>The number of files in the package.</returns>
    public static int Write(Stream destination, string diagnostics, AppPaths paths, string? userProfile)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(paths);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        Add(archive, DiagnosticsEntry, diagnostics);
        int count = 1;

        // Daily files, named by date: the newest sort last. The tray app writes to today's while we read it.
        if (Directory.Exists(paths.Logs))
        {
            foreach (string file in Directory.EnumerateFiles(paths.Logs, "rigshift-*.log").Order(StringComparer.OrdinalIgnoreCase).TakeLast(Logs))
            {
                count += AddFile(archive, "logs/" + Path.GetFileName(file), file, userProfile);
            }
        }

        count += AddFile(archive, BackupArchive.SettingsEntry, paths.SettingsFile, userProfile);
        count += AddFile(archive, BackupArchive.GamesEntry, Path.Combine(paths.DataDirectory, BackupArchive.GamesEntry), userProfile);
        if (Directory.Exists(paths.Profiles))
        {
            foreach (string file in Directory.EnumerateFiles(paths.Profiles, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                count += AddFile(archive, BackupArchive.ProfilesFolder + Path.GetFileName(file), file, userProfile);
            }
        }

        return count;
    }

    private static int AddFile(ZipArchive archive, string entry, string file, string? userProfile)
    {
        if (!File.Exists(file))
        {
            return 0;
        }

        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        Add(archive, entry, DiagnosticsReport.Anonymize(reader.ReadToEnd(), userProfile));
        return 1;
    }

    private static void Add(ZipArchive archive, string entry, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(entry, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(text);
    }
}
