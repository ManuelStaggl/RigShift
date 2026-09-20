using System.IO.Compression;

namespace RigShift.Core.Storage;

/// <summary>
/// Reads files RigShift did not write itself – a backup somebody passed on, the store clients' manifests – with an
/// upper bound. Every one of them is a few kilobytes when genuine; without a bound a prepared file of gigabytes, or a
/// ZIP entry that unpacks to that, would simply be read into memory.
/// </summary>
public static class BoundedRead
{
    /// <summary>Far above any genuine profile, settings file or store manifest.</summary>
    public const int DefaultLimit = 1024 * 1024;

    /// <summary>The file's text. Fails as <see cref="IOException"/> when the file is larger than <paramref name="limit"/> bytes.</summary>
    public static string Text(string file, int limit = DefaultLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        using FileStream stream = new(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > limit)
        {
            throw new IOException($"'{file}' is larger than {limit} bytes and was not read.");
        }

        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// The unpacked entry. The size in the ZIP directory is only a claim, so the bytes are counted while unpacking.
    /// Fails as <see cref="InvalidDataException"/> beyond <paramref name="limit"/>.
    /// </summary>
    public static MemoryStream Entry(ZipArchiveEntry entry, int limit = DefaultLimit)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Length > limit)
        {
            throw new InvalidDataException($"'{entry.FullName}' is larger than {limit} bytes.");
        }

        var buffer = new MemoryStream();
        using Stream source = entry.Open();
        byte[] chunk = new byte[16 * 1024];
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            if (buffer.Length + read > limit)
            {
                buffer.Dispose();
                throw new InvalidDataException($"'{entry.FullName}' unpacks to more than {limit} bytes.");
            }

            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }
}
