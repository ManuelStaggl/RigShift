using RigShift.Core.Abstractions;
using RigShift.Core.Games;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>
/// The game settings file on disk. The first and only place where RigShift writes into a game's files, so every write
/// is preceded by a copy of the whole file under <c>%AppData%\RigShift\backups\game-settings</c> and checked afterwards.
/// </summary>
public sealed class GameSettingsFiles(string backupDirectory, TimeProvider time, ILogger log) : IGameSettingsFiles
{
    /// <summary>Backups kept per file name; enough to go back a few sessions, not a growing pile.</summary>
    public const int BackupsKept = 10;

    private readonly ILogger _log = log.ForContext<GameSettingsFiles>();

    public async Task<IReadOnlyDictionary<string, string>> SwapAsync(
        string file, IReadOnlyList<string> keys, IReadOnlyDictionary<string, string> values, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(values);

        bool ini = GameSettings.IsIni(file);
        IReadOnlyDictionary<string, string> Read(byte[] content, IEnumerable<string> wanted) =>
            ini ? GameSettingsIni.Read(content, wanted) : GameSettingsJson.Read(content, wanted);

        byte[] original = await File.ReadAllBytesAsync(file, cancellationToken);
        IReadOnlyDictionary<string, string> before = Read(original, keys);
        Dictionary<string, string> changes = values
            .Where(v => before.TryGetValue(v.Key, out string? current) && current != v.Value)
            .ToDictionary(v => v.Key, v => v.Value, StringComparer.Ordinal);
        if (changes.Count == 0)
        {
            _log.Information("Game settings {File}: {Count} value(s) read, nothing to change", file, before.Count);
            return before;
        }

        byte[] changed = ini ? GameSettingsIni.Replace(original, changes) : GameSettingsJson.Replace(original, changes);
        string backup = await BackupAsync(file, original, cancellationToken);
        await AtomicFile.WriteAsync(file, stream => stream.WriteAsync(changed, cancellationToken).AsTask(), cancellationToken);

        IReadOnlyDictionary<string, string> after = Read(await File.ReadAllBytesAsync(file, cancellationToken), changes.Keys);
        if (changes.Any(c => !after.TryGetValue(c.Key, out string? written) || written != c.Value))
        {
            await AtomicFile.WriteAsync(file, stream => stream.WriteAsync(original, CancellationToken.None).AsTask(), CancellationToken.None);
            throw new InvalidDataException($"The values in {file} did not read back as written; the file was put back as it was.");
        }

        _log.Information("Game settings {File}: {Count} value(s) written, backup {Backup}", file, changes.Count, backup);
        return before;
    }

    private async Task<string> BackupAsync(string file, byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(backupDirectory);
        string name = Path.GetFileNameWithoutExtension(file);
        string stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss-fff", System.Globalization.CultureInfo.InvariantCulture);
        string backup = Path.Combine(backupDirectory, $"{name}-{stamp}{Path.GetExtension(file)}");
        await File.WriteAllBytesAsync(backup, content, cancellationToken);

        // The stamp sorts by time, so the oldest come first.
        foreach (string old in Directory.GetFiles(backupDirectory, name + "-*").Order(StringComparer.Ordinal).SkipLast(BackupsKept))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warning(ex, "Old game settings backup {Backup} could not be deleted", old);
            }
        }

        return backup;
    }
}
