using System.IO.Compression;
using System.Text.Json;
using RigShift.Core.Profiles;
using RigShift.Core.Settings;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>What a backup holds, read without touching the data folder.</summary>
public sealed record BackupContent(IReadOnlyList<Profile> Profiles, AppSettings? Settings);

/// <summary>
/// Profiles and settings as one ZIP file (1.7.0): <c>profiles/&lt;guid&gt;.json</c> plus <c>settings.json</c>, exactly
/// the files RigShift keeps in <c>%AppData%\RigShift</c>. Logs are not included. Restoring replaces every profile and the
/// settings; profile files are written under the id inside them, never under the name in the archive.
/// </summary>
public static class BackupArchive
{
    public const string SettingsEntry = "settings.json";
    public const string ProfilesFolder = "profiles/";

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

        string settings = Path.Combine(dataDirectory, SettingsEntry);
        if (File.Exists(settings))
        {
            archive.CreateEntryFromFile(settings, SettingsEntry);
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

            if (profiles.Count == 0 && settings is null)
            {
                throw new InvalidDataException("The archive contains no profiles and no settings.");
            }

            var ids = new HashSet<Guid>();
            foreach (Profile profile in profiles)
            {
                if (!ids.Add(profile.Id))
                {
                    throw new InvalidDataException($"Profile '{profile.Name}' appears twice.");
                }
            }

            return new BackupContent(profiles, settings);
        }
    }

    /// <summary>
    /// Replaces the profiles in <paramref name="dataDirectory"/> with those of <paramref name="content"/> and the settings
    /// file when the backup has one. Existing profile files go first, so a profile that is not in the backup is gone.
    /// </summary>
    public static void Restore(string dataDirectory, BackupContent content, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(log);

        string profiles = Path.Combine(dataDirectory, "profiles");
        Directory.CreateDirectory(profiles);
        int removed = 0;
        foreach (string file in Directory.EnumerateFiles(profiles, "*.json").ToList())
        {
            File.Delete(file);
            removed++;
        }

        foreach (Profile profile in content.Profiles)
        {
            string target = Path.Combine(profiles, profile.Id.ToString("D") + ".json");
            using FileStream stream = File.Create(target);
            JsonSerializer.Serialize(stream, new ProfileDocument(JsonProfileStore.CurrentSchemaVersion, profile), ProfileJsonContext.Default.ProfileDocument);
        }

        if (content.Settings is { } settings)
        {
            string file = Path.Combine(dataDirectory, SettingsEntry);
            string temp = file + ".tmp";
            using (FileStream stream = File.Create(temp))
            {
                JsonSerializer.Serialize(stream, settings, SettingsJsonContext.Default.AppSettings);
            }

            File.Move(temp, file, overwrite: true);
        }

        log.ForContext(typeof(BackupArchive)).Information(
            "Backup restored to {Directory}: {Profiles} profile(s) written, {Removed} old file(s) removed, settings {Settings}",
            dataDirectory, content.Profiles.Count, removed, content.Settings is null ? "kept" : "replaced");
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

        if (string.IsNullOrWhiteSpace(profile.Name) || profile.Displays is null)
        {
            throw new InvalidDataException($"'{entry.FullName}' is not a complete profile.");
        }

        return profile.WithMigratedConfirmation();
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
