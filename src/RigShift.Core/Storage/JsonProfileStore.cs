using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// Profiles as one JSON file per profile: <c>&lt;directory&gt;\&lt;guid&gt;.json</c>, wrapped with a schema version.
/// Plain .NET file I/O, therefore in Core and testable against a temp directory.
/// A broken, locked or newer-schema file is skipped with a warning instead of hiding every other profile, and reported in
/// <see cref="LoadResult.Unreadable"/>.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _directory;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    /// <param name="time">Clock for the retry pause; <see cref="TimeProvider.System"/> when omitted.</param>
    public JsonProfileStore(string directory, ILogger log, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _directory = Path.GetFullPath(directory);
        _log = log.ForContext<JsonProfileStore>();
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>%AppData%\RigShift\profiles</c> – outside the Velopack install folder, which uninstall deletes.</summary>
    public static string DefaultDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift", "profiles"));

    public async Task<LoadResult> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            _log.Information("Profile directory {Directory} does not exist yet", _directory);
            return LoadResult.Empty;
        }

        var profiles = new List<Profile>();
        var unreadable = new List<UnreadableProfileFile>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            string name = Path.GetFileName(file);
            try
            {
                ProfileDocument? document = await ReadWithRetryAsync(file, cancellationToken);

                if (document?.Profile is null)
                {
                    _log.Warning("Profile file {File} is empty, skipped", file);
                    unreadable.Add(new UnreadableProfileFile(name, "The file contains no profile."));
                }
                else if (document.SchemaVersion > CurrentSchemaVersion)
                {
                    _log.Warning("Profile file {File} has schema version {SchemaVersion} (supported: {Supported}), skipped",
                        file, document.SchemaVersion, CurrentSchemaVersion);
                    unreadable.Add(new UnreadableProfileFile(name, "The file is from a newer RigShift version."));
                }
                else
                {
                    Profile profile = document.Profile.WithMigratedConfirmation();
                    if (!ReferenceEquals(profile, document.Profile))
                    {
                        _log.Information("Profile {Profile}: confirmation time {Seconds} s replaced by \"switch without asking\" = {WithoutAsking}",
                            profile.Name, document.Profile.ConfirmTimeoutSeconds, profile.SwitchWithoutAsking);
                    }

                    profiles.Add(profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Profile file {File} could not be read, skipped", file);
                unreadable.Add(new UnreadableProfileFile(name, ex.Message));
            }
        }

        _log.Information("Loaded {Count} profiles from {Directory}, {Unreadable} file(s) unreadable",
            profiles.Count, _directory, unreadable.Count);
        return new LoadResult(profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList(), unreadable);
    }

    /// <summary>
    /// Opens without blocking writers or deleters (an editor or antivirus holding the file must not hide it) and tries a
    /// second time after <see cref="RetryDelay"/>, because such locks are usually brief.
    /// </summary>
    private async Task<ProfileDocument?> ReadWithRetryAsync(string file, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(file, cancellationToken);
        }
        catch (IOException ex)
        {
            _log.Information(ex, "Profile file {File} is not readable right now, retrying in {Delay} ms", file, RetryDelay.TotalMilliseconds);
            await Task.Delay(RetryDelay, _time, cancellationToken);
            return await ReadAsync(file, cancellationToken);
        }
    }

    private static async Task<ProfileDocument?> ReadAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, ProfileJsonContext.Default.ProfileDocument, cancellationToken);
    }

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        Directory.CreateDirectory(_directory);

        string target = FileFor(profile.Id);
        string temp = target + ".tmp";

        // Write to a temp file and move it over the original, so a crash never leaves a half-written profile.
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream, new ProfileDocument(CurrentSchemaVersion, profile), ProfileJsonContext.Default.ProfileDocument, cancellationToken);
        }

        File.Move(temp, target, overwrite: true);
        _log.Information("Saved profile {Profile} to {File}", profile.Name, target);
    }

    public Task DeleteAsync(Guid profileId, CancellationToken cancellationToken)
    {
        string file = FileFor(profileId);
        if (File.Exists(file))
        {
            File.Delete(file);
            _log.Information("Deleted profile file {File}", file);
        }

        return Task.CompletedTask;
    }

    private string FileFor(Guid id) => Path.GetFullPath(Path.Combine(_directory, id.ToString("D") + ".json"));
}

internal sealed record ProfileDocument(int SchemaVersion, Profile Profile);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(ProfileDocument))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
