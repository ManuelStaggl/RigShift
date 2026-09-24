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
                ProfileDocument? document = await JsonFile.ReadWithRetryAsync(
                    file, ProfileJsonContext.Default.ProfileDocument, _time, _log, cancellationToken);

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
                else if (StoredDataCheck.Problem(document.Profile) is { } problem)
                {
                    _log.Warning("Profile file {File} is not a usable profile ({Problem}), skipped", file, problem);
                    unreadable.Add(new UnreadableProfileFile(name, problem));
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

    public async Task SaveAsync(Profile profile, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        string target = FileFor(profile.Id);

        // Written next to the original and moved over it, so a crash never leaves a half-written profile.
        await AtomicFile.WriteAsync(
            target,
            stream => JsonSerializer.SerializeAsync(
                stream, new ProfileDocument(CurrentSchemaVersion, profile), ProfileJsonContext.Default.ProfileDocument, cancellationToken),
            cancellationToken);
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
