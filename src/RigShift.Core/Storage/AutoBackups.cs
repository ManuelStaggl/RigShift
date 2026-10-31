namespace RigShift.Core.Storage;

/// <summary>
/// Backups RigShift writes by itself before it replaces or removes data: <c>backups\auto-*.zip</c> in the data folder,
/// in the format of <see cref="BackupArchive"/>. Only the newest <see cref="Kept"/> stay; backups the user saves go
/// wherever the user puts them and are never touched.
/// </summary>
public static class AutoBackups
{
    public const int Kept = 5;

    public static string Folder(string dataDirectory) => Path.Combine(dataDirectory, "backups");

    /// <summary>Writes a backup named after <paramref name="reason"/> and removes the oldest ones beyond <see cref="Kept"/>.</summary>
    /// <returns>The path of the new backup.</returns>
    public static string Write(string dataDirectory, string reason, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);

        string folder = Folder(dataDirectory);
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, $"auto-{now.UtcDateTime:yyyyMMdd-HHmmss-fff}-{reason}.zip");
        try
        {
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            BackupArchive.Write(dataDirectory, stream);
        }
        catch
        {
            // A half-written ZIP would count as one of the kept backups.
            File.Delete(file);
            throw;
        }

        // The timestamp in the name sorts them by age.
        foreach (string old in Directory.GetFiles(folder, "auto-*.zip").Order(StringComparer.Ordinal).SkipLast(Kept))
        {
            File.Delete(old);
        }

        return file;
    }
}
