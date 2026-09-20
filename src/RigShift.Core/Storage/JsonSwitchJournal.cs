using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// The switch journal as a single file <c>&lt;directory&gt;\pending-switch.json</c>. Plain .NET file I/O, therefore in
/// Core and testable against a temp directory.
/// A broken or newer-schema record reads as "nothing recorded": offering to restore a layout we cannot read would be
/// worse than offering nothing.
/// </summary>
public sealed class JsonSwitchJournal : ISwitchJournal
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "pending-switch.json";

    private readonly string _file;
    private readonly ILogger _log;

    public JsonSwitchJournal(string directory, ILogger log)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _file = Path.GetFullPath(Path.Combine(directory, FileName));
        _log = log.ForContext<JsonSwitchJournal>();
    }

    /// <summary><c>%AppData%\RigShift</c> – outside the Velopack install folder, which uninstall deletes.</summary>
    public static string DefaultDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift"));

    public async Task BeginAsync(InterruptedSwitch entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);

        try
        {
            // On the disk before the driver is called: a hang and reset right after would otherwise leave an empty record.
            await AtomicFile.WriteAsync(
                _file,
                stream => JsonSerializer.SerializeAsync(
                    stream, new JournalDocument(CurrentSchemaVersion, entry), JournalJsonContext.Default.JournalDocument, cancellationToken),
                cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A switch that cannot be journalled still has the in-process rollback; failing it here would be worse.
            _log.Warning(exception, "Could not record the running switch in {File}", _file);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(_file))
            {
                File.Delete(_file);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind, the record asks about a switch that did finish – annoying, not harmful.
            _log.Warning(exception, "Could not remove the switch record {File}", _file);
        }

        return Task.CompletedTask;
    }

    public async Task<InterruptedSwitch?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_file))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                _file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
            JournalDocument? document = await JsonSerializer.DeserializeAsync(
                stream, JournalJsonContext.Default.JournalDocument, cancellationToken);

            if (document?.Switch is null)
            {
                _log.Warning("Switch record {File} contains no switch, ignored", _file);
                return null;
            }

            if (document.SchemaVersion > CurrentSchemaVersion)
            {
                _log.Warning("Switch record {File} was written by a newer version (schema {Version}), ignored", _file, document.SchemaVersion);
                return null;
            }

            return document.Switch;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.Warning(exception, "Switch record {File} is unreadable, ignored", _file);
            return null;
        }
    }
}

internal sealed record JournalDocument(int SchemaVersion, InterruptedSwitch Switch);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(JournalDocument))]
internal sealed partial class JournalJsonContext : JsonSerializerContext;
