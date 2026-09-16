using System.Text.Json;
using System.Text.Json.Serialization;
using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// Game entries as one JSON file, <c>&lt;directory&gt;\games.json</c>, wrapped with a schema version. One file rather
/// than one per game as with profiles: games are few, are always shown as a list, and reordering them must not mean
/// touching a dozen files. Plain .NET file I/O, therefore in Core and testable against a temp directory.
/// </summary>
public sealed class JsonGameStore : IGameStore
{
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "games.json";

    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly string _file;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    /// <param name="directory">Folder holding <see cref="FileName"/>.</param>
    /// <param name="time">Clock for the retry pause; <see cref="TimeProvider.System"/> when omitted.</param>
    public JsonGameStore(string directory, ILogger log, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(log);
        _file = Path.GetFullPath(Path.Combine(directory, FileName));
        _log = log.ForContext<JsonGameStore>();
        _time = time ?? TimeProvider.System;
    }

    /// <summary><c>%AppData%\RigShift</c> – outside the Velopack install folder, which uninstall deletes.</summary>
    public static string DefaultDirectory => Path.GetFullPath(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RigShift"));

    public async Task<GameLoadResult> LoadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_file))
        {
            _log.Information("Game file {File} does not exist yet", _file);
            return GameLoadResult.Empty;
        }

        try
        {
            GameDocument? document = await ReadWithRetryAsync(cancellationToken);
            if (document is null)
            {
                _log.Warning("Game file {File} is empty", _file);
                return new GameLoadResult([], "The file contains no games.");
            }

            if (document.SchemaVersion > CurrentSchemaVersion)
            {
                _log.Warning("Game file {File} has schema version {SchemaVersion} (supported: {Supported}), skipped",
                    _file, document.SchemaVersion, CurrentSchemaVersion);
                return new GameLoadResult([], "The file is from a newer RigShift version.");
            }

            IReadOnlyList<GameEntry> games = [.. (document.Games ?? [])
                .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)];
            _log.Information("Loaded {Count} games from {File}", games.Count, _file);
            return new GameLoadResult(games, null);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Game file {File} could not be read", _file);
            return new GameLoadResult([], ex.Message);
        }
    }

    public async Task SaveAsync(GameEntry game, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(game);
        GameLoadResult loaded = await LoadAllAsync(cancellationToken);
        if (!loaded.IsComplete)
        {
            // Writing now would drop every entry the unreadable file still holds.
            throw new InvalidOperationException($"The game file could not be read, so it must not be overwritten: {loaded.Unreadable}");
        }

        List<GameEntry> games = [.. loaded.Games.Where(g => g.Id != game.Id), game];
        await WriteAsync(games, cancellationToken);
        _log.Information("Saved game {Game} to {File}", game.Name, _file);
    }

    public async Task DeleteAsync(Guid gameId, CancellationToken cancellationToken)
    {
        GameLoadResult loaded = await LoadAllAsync(cancellationToken);
        if (!loaded.IsComplete)
        {
            throw new InvalidOperationException($"The game file could not be read, so it must not be overwritten: {loaded.Unreadable}");
        }

        List<GameEntry> games = [.. loaded.Games.Where(g => g.Id != gameId)];
        if (games.Count == loaded.Games.Count)
        {
            return;
        }

        await WriteAsync(games, cancellationToken);
        _log.Information("Deleted game {GameId} from {File}", gameId, _file);
    }

    private async Task WriteAsync(IReadOnlyList<GameEntry> games, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        string temp = _file + ".tmp";

        // Write to a temp file and move it over the original, so a crash never leaves a half-written list.
        await using (FileStream stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                stream, new GameDocument(CurrentSchemaVersion, games), GameJsonContext.Default.GameDocument, cancellationToken);
        }

        File.Move(temp, _file, overwrite: true);
    }

    /// <summary>
    /// Opens without blocking writers or deleters (an editor or antivirus holding the file must not hide the games)
    /// and tries a second time after <see cref="RetryDelay"/>, because such locks are usually brief.
    /// </summary>
    private async Task<GameDocument?> ReadWithRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            _log.Information(ex, "Game file {File} is not readable right now, retrying in {Delay} ms", _file, RetryDelay.TotalMilliseconds);
            await Task.Delay(RetryDelay, _time, cancellationToken);
            return await ReadAsync(cancellationToken);
        }
    }

    private async Task<GameDocument?> ReadAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, GameJsonContext.Default.GameDocument, cancellationToken);
    }
}

internal sealed record GameDocument(int SchemaVersion, IReadOnlyList<GameEntry>? Games);

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(GameDocument))]
internal sealed partial class GameJsonContext : JsonSerializerContext;
