using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Serilog;

namespace RigShift.Core.Storage;

/// <summary>Reads the JSON files RigShift writes with <see cref="AtomicFile"/>.</summary>
internal static class JsonFile
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Opens without blocking writers or deleters (an editor or antivirus holding the file must not hide it) and tries a
    /// second time after <see cref="RetryDelay"/>, because such locks are usually brief. The second failure is thrown.
    /// </summary>
    public static async Task<T?> ReadWithRetryAsync<T>(
        string file, JsonTypeInfo<T> type, TimeProvider time, ILogger log, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadAsync(file, type, cancellationToken);
        }
        catch (IOException ex)
        {
            log.Information(ex, "File {File} is not readable right now, retrying in {Delay} ms", file, RetryDelay.TotalMilliseconds);
            await Task.Delay(RetryDelay, time, cancellationToken);
            return await ReadAsync(file, type, cancellationToken);
        }
    }

    private static async Task<T?> ReadAsync<T>(string file, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, bufferSize: 4096, useAsync: true);
        return await JsonSerializer.DeserializeAsync(stream, type, cancellationToken);
    }
}
