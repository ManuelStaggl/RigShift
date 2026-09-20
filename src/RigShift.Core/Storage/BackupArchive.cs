using System.IO.Compression;
using System.Text.Json;
using RigShift.Core.Games;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>What a backup holds, read without touching the data folder.</summary>
/// <param name="Games">The games, or <c>null</c> for a backup from before they were included: restoring leaves the games alone.</param>
public sealed record BackupContent(IReadOnlyList<Profile> Profiles, AppSettings? Settings, IReadOnlyList<GameEntry>? Games = null);

/// <summary>
/// Profiles, games and settings as one ZIP file: <c>profiles/&lt;guid&gt;.json</c>, <c>games.json</c> and
/// <c>settings.json</c>, exactly the files RigShift keeps in <c>%AppData%\RigShift</c>. Logs are not included.
/// Restoring replaces every profile and whichever of the other two the backup has; profile files are written under
/// the id inside them, never under the name in the archive.
/// </summary>
public static class BackupArchive
{
    public const string SettingsEntry = "settings.json";
    public const string GamesEntry = JsonGameStore.FileName;
    public const string ProfilesFolder = "profiles/";

    /// <summary>Where a restore collects the new profile files before any existing file is touched.</summary>
    private const string StagingFolder = "profiles.restore";

    /// <summary>Writes the backup of <paramref name="dataDirectory"/> to <paramref name="destination"/>.</summary>
    /// <returns>The number of profile files written.</returns>
    public static int Write(string dataDirectory, Stream destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(destination);

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        int count = 0;
        string profiles = Path.Combine(dataDirectory, "profiles");
        if (Directory.Exists(profiles))
        {
            foreach (string file in Directory.EnumerateFiles(profiles, "*.json").Order(StringComparer.OrdinalIgnoreCase))
            {
                archive.CreateEntryFromFile(file, ProfilesFolder + Path.GetFileName(file));
                count++;
            }
        }

        foreach (string name in (string[])[SettingsEntry, GamesEntry])
        {
            string file = Path.Combine(dataDirectory, name);
            if (File.Exists(file))
            {
                archive.CreateEntryFromFile(file, name);
            }
        }

        return count;
    }

    /// <summary>Reads and checks the archive. Anything that is not a RigShift backup fails as <see cref="InvalidDataException"/>.</summary>
    public static BackupContent Inspect(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("The file is not a ZIP archive.", ex);
        }

        using (archive)
        {
            var profiles = new List<Profile>();
            AppSettings? settings = null;
            IReadOnlyList<GameEntry>? games = null;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string name = entry.FullName.Replace('\\', '/');
                if (name.EndsWith('/'))
                {
                    continue; // A folder entry.
                }

                if (string.Equals(name, SettingsEntry, StringComparison.OrdinalIgnoreCase))
                {
                    settings = ReadSettings(entry);
                }
                else if (string.Equals(name, GamesEntry, StringComparison.OrdinalIgnoreCase))
                {
                    games = ReadGames(entry);
                }
                else if (name.StartsWith(ProfilesFolder, StringComparison.OrdinalIgnoreCase)
                    && name.Length > ProfilesFolder.Length
                    && !name.AsSpan(ProfilesFolder.Length).Contains('/')
                    && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                {
                    profiles.Add(ReadProfile(entry));
                }
                else
                {
                    throw new InvalidDataException($"'{entry.FullName}' does not belong to a RigShift backup.");
                }
            }

            if (profiles.Count == 0 && settings is null && games is null)
            {
                throw new InvalidDataException("The archive contains no profiles, no games and no settings.");
            }

            var ids = new HashSet<Guid>();
            foreach (Profile profile in profiles)
            {
                if (!ids.Add(profile.Id))
                {
                    throw new InvalidDataException($"Profile '{profile.Name}' appears twice.");
                }
            }

            return new BackupContent(profiles, settings, games);
        }
    }

    /// <summary>
    /// Replaces the profiles in <paramref name="dataDirectory"/> with those of <paramref name="content"/>, and the
    /// settings and the games when the backup has them. A profile that is not in the backup is gone afterwards.
    /// Everything new is written to the side first; existing files are only touched once all of it is safely on disk,
    /// and old profiles are removed last. A failure on the way – disk full, a virus scanner holding a file – therefore
    /// leaves either the old state or old and new profiles side by side, never an empty folder.
    /// </summary>
    public static void Restore(string dataDirectory, BackupContent content, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(log);

        string profiles = Path.Combine(dataDirectory, "profiles");
        string staging = Path.Combine(dataDirectory, StagingFolder);
        string settingsFile = Path.Combine(dataDirectory, SettingsEntry);
        string gamesFile = Path.Combine(dataDirectory, GamesEntry);
        Directory.CreateDirectory(profiles);
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true); // Left over from a restore that did not finish.
        }

        Directory.CreateDirectory(staging);
        try
        {
            // 1. Everything new, next to the old.
            var staged = new List<string>(content.Profiles.Count);
            foreach (Profile profile in content.Profiles)
            {
                string name = profile.Id.ToString("D") + ".json";
                WriteThrough(Path.Combine(staging, name), stream => JsonSerializer.Serialize(
                    stream, new ProfileDocument(JsonProfileStore.CurrentSchemaVersion, profile), ProfileJsonContext.Default.ProfileDocument));
                staged.Add(name);
            }

            if (content.Settings is { } settings)
            {
                WriteThrough(settingsFile + ".restore", stream => JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings));
            }

            if (content.Games is { } games)
            {
                WriteThrough(gamesFile + ".restore", stream => JsonSerializer.Serialize(
                    stream, new GameDocument(JsonGameStore.CurrentSchemaVersion, games), GameJsonContext.Default.GameDocument));
            }

            // 2. Into place, file by file; each move replaces its target in one step.
            foreach (string name in staged)
            {
                File.Move(Path.Combine(staging, name), Path.Combine(profiles, name), overwrite: true);
            }

            if (content.Settings is not null)
            {
                File.Move(settingsFile + ".restore", settingsFile, overwrite: true);
            }

            if (content.Games is not null)
            {
                File.Move(gamesFile + ".restore", gamesFile, overwrite: true);
            }

            // 3. Only now the profiles the backup does not know.
            var keep = new HashSet<string>(staged, StringComparer.OrdinalIgnoreCase);
            int removed = 0;
            foreach (string file in Directory.EnumerateFiles(profiles, "*.json").ToList())
            {
                if (!keep.Contains(Path.GetFileName(file)))
                {
                    File.Delete(file);
                    removed++;
                }
            }

            log.ForContext(typeof(BackupArchive)).Information(
                "Backup restored to {Directory}: {Profiles} profile(s) written, {Removed} old file(s) removed, settings {Settings}, games {Games}",
                dataDirectory, content.Profiles.Count, removed,
                content.Settings is null ? "kept" : "replaced", content.Games is null ? "kept" : "replaced");
        }
        finally
        {
            TryCleanUp(staging, settingsFile + ".restore", gamesFile + ".restore");
        }
    }

    /// <summary>Written and flushed to the disk, not just to the cache: the next step relies on the file being there.</summary>
    private static void WriteThrough(string file, Action<FileStream> write)
    {
        using FileStream stream = File.Create(file);
        write(stream);
        stream.Flush(flushToDisk: true);
    }

    private static void TryCleanUp(string staging, params string[] files)
    {
        try
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }

            foreach (string file in files)
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leftovers are removed by the next restore; they are never read.
        }
    }

    private static Profile ReadProfile(ZipArchiveEntry entry)
    {
        ProfileDocument? document;
        try
        {
            using Stream stream = BoundedRead.Entry(entry);
            document = JsonSerializer.Deserialize(stream, ProfileJsonContext.Default.ProfileDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"'{entry.FullName}' is not a RigShift profile.", ex);
        }

        if (document?.Profile is not { } profile)
        {
            throw new InvalidDataException($"'{entry.FullName}' contains no profile.");
        }

        if (document.SchemaVersion > JsonProfileStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"'{entry.FullName}' is from a newer RigShift version.");
        }

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Displays is null || profile.Apps is null)
        {
            throw new InvalidDataException($"'{entry.FullName}' is not a complete profile.");
        }

        return profile.WithMigratedConfirmation();
    }

    private static IReadOnlyList<GameEntry> ReadGames(ZipArchiveEntry entry)
    {
        GameDocument? document;
        try
        {
            using Stream stream = BoundedRead.Entry(entry);
            document = JsonSerializer.Deserialize(stream, GameJsonContext.Default.GameDocument);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("games.json is not a RigShift games file.", ex);
        }

        if (document is null)
        {
            throw new InvalidDataException("games.json is empty.");
        }

        if (document.SchemaVersion > JsonGameStore.CurrentSchemaVersion)
        {
            throw new InvalidDataException("games.json is from a newer RigShift version.");
        }

        IReadOnlyList<GameEntry> games = document.Games ?? [];
        var ids = new HashSet<Guid>();
        foreach (GameEntry game in games)
        {
            if (game is null || string.IsNullOrWhiteSpace(game.Name) || game.Launch is null || game.Apps is null)
            {
                throw new InvalidDataException("games.json contains an incomplete game.");
            }

            if (!ids.Add(game.Id))
            {
                throw new InvalidDataException($"Game '{game.Name}' appears twice.");
            }
        }

        return games;
    }

    private static AppSettings ReadSettings(ZipArchiveEntry entry)
    {
        AppSettings? settings;
        try
        {
            using Stream stream = BoundedRead.Entry(entry);
            settings = JsonSerializer.Deserialize(stream, SettingsJsonContext.Default.AppSettings);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("settings.json is not a RigShift settings file.", ex);
        }

        if (settings is null)
        {
            throw new InvalidDataException("settings.json is empty.");
        }

        if (settings.SchemaVersion > AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException("settings.json is from a newer RigShift version.");
        }

        return settings;
    }
}
