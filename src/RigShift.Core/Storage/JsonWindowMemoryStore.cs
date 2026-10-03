using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Profiles;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// The remembered windows of all profiles as a single file <c>&lt;directory&gt;\window-memory.json</c>. Read once and
/// kept in memory; a broken or newer-schema file reads as "nothing remembered" and is overwritten by the next switch –
/// it holds nothing the user wrote.
/// </summary>
public sealed class JsonWindowMemoryStore : IWindowMemoryStore, IDisposable
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "window-memory.json";

    private readonly string _file;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<Guid, RememberedWindows>? _profiles;

    public JsonWindowMemoryStore(string directory, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _file = Path.GetFullPath(Path.Combine(directory, FileName));
        _log = log.ForContext<JsonWindowMemoryStore>();
    }

    public void Dispose() => _gate.Dispose();

    public async Task<RememberedWindows?> LoadAsync(Guid profileId, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<Guid, RememberedWindows> profiles = _profiles ??= await ReadAsync(cancellationToken);
            return profiles.GetValueOrDefault(profileId);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(Guid profileId, RememberedWindows windows, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(windows);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Dictionary<Guid, RememberedWindows> profiles = _profiles ??= await ReadAsync(cancellationToken);
            profiles[profileId] = windows;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                await AtomicFile.WriteAsync(
                    _file,
                    stream => JsonSerializer.SerializeAsync(
                        stream, new WindowMemoryDocument(CurrentSchemaVersion, profiles), WindowMemoryJsonContext.Default.WindowMemoryDocument, cancellationToken),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Still remembered for as long as RigShift runs, which is the case that matters.
                _log.Warning(exception, "Could not write the remembered windows to {File}", _file);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Dictionary<Guid, RememberedWindows>> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_file))
        {
            return [];
        }

        try
        {
            await using var stream = new FileStream(
                _file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            WindowMemoryDocument? document = await JsonSerializer.DeserializeAsync(
                stream, WindowMemoryJsonContext.Default.WindowMemoryDocument, cancellationToken);

            if (document?.Profiles is null)
            {
                _log.Warning("Window memory {File} contains no profiles, ignored", _file);
                return [];
            }

            if (document.SchemaVersion > CurrentSchemaVersion)
            {
                _log.Warning("Window memory {File} was written by a newer version (schema {Version}), ignored", _file, document.SchemaVersion);
                return [];
            }

            // Entries a hand-edited file left incomplete would fail later, in the middle of a switch.
            return document.Profiles
                .Where(p => p.Value?.Windows is not null && p.Value.Windows.All(w => w?.Placement?.ProcessName is not null))
                .ToDictionary(p => p.Key, p => p.Value);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warning(exception, "Window memory {File} is unreadable, ignored", _file);
            return [];
        }
    }
}

internal sealed record WindowMemoryDocument(int SchemaVersion, Dictionary<Guid, RememberedWindows> Profiles);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WindowMemoryDocument))]
internal sealed partial class WindowMemoryJsonContext : JsonSerializerContext;
