using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// Profiles as one JSON file per profile: <c>&lt;directory&gt;\&lt;guid&gt;.json</c>, wrapped with a schema version.
/// Plain .NET file I/O, therefore in Core and testable against a temp directory.
/// A broken or newer-schema file is skipped with a warning instead of hiding every other profile.
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    public const int CurrentSchemaVersion = 1;

    private readonly string _directory;
    private readonly ILogger _log;

    public JsonProfileStore(string directory, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _directory = Path.GetFullPath(directory);
        _log = log.ForContext<JsonProfileStore>();
    }

    /// <summary><c>%LocalAppData%\RigShift\profiles</c>.</summary>
    public static string DefaultDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RigShift", "profiles"));

    public async Task<IReadOnlyList<Profile>> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory))
        {
            _log.Information("Profile directory {Directory} does not exist yet", _directory);
            return [];
        }

        var profiles = new List<Profile>();
        foreach (string file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            try
            {
                await using FileStream stream = File.OpenRead(file);
                ProfileDocument? document = await JsonSerializer.DeserializeAsync(
                    stream, ProfileJsonContext.Default.ProfileDocument, cancellationToken);

                if (document?.Profile is null)
                {
                    _log.Warning("Profile file {File} is empty, skipped", file);
                }
                else if (document.SchemaVersion > CurrentSchemaVersion)
                {
                    _log.Warning("Profile file {File} has schema version {SchemaVersion} (supported: {Supported}), skipped",
                        file, document.SchemaVersion, CurrentSchemaVersion);
                }
                else
                {
                    profiles.Add(document.Profile);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Profile file {File} could not be read, skipped", file);
            }
        }

        _log.Information("Loaded {Count} profiles from {Directory}", profiles.Count, _directory);
        return profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
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
