namespace RigShift.Core.Storage;

/// <summary>
/// Replaces a file in one step: written next to it, flushed to the disk, then moved over it. The flush is the point –
/// without it the move can reach the disk before the content does, and a driver hang or power loss right after leaves
/// an empty file under the final name. The switch journal is written immediately before the display driver is called.
/// </summary>
public static class AtomicFile
{
    public static async Task WriteAsync(string file, Func<Stream, Task> write, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();

        Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");

        // Its own name per write: two writers sharing one temp file truncate each other's content.
        string temp = $"{file}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, bufferSize: 4096, useAsync: true))
            {
                await write(stream);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, file, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A stray temp file is harmless; the failure that brought us here is what the caller needs to see.
        }
    }
}
