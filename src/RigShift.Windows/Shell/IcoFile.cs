using System.Buffers.Binary;

namespace RigShift.Windows.Shell;

/// <summary>
/// Builds a multi-size <c>.ico</c> whose frames are PNG images – the form Windows reads for every size since Vista,
/// so each size can be drawn on its own instead of the shell scaling one down.
/// </summary>
public static class IcoFile
{
    private const int HeaderSize = 6;
    private const int EntrySize = 16;

    /// <param name="frames">One PNG per size, 1 to 256 pixels square; the shell picks the closest one.</param>
    public static byte[] Build(IReadOnlyList<(int Size, byte[] Png)> frames)
    {
        ArgumentNullException.ThrowIfNull(frames);
        if (frames.Count is 0 or > ushort.MaxValue)
        {
            throw new ArgumentException("An icon needs at least one frame.", nameof(frames));
        }

        int offset = HeaderSize + EntrySize * frames.Count;
        byte[] file = new byte[offset + frames.Sum(f => f.Png.Length)];
        Span<byte> span = file;

        BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)frames.Count);

        for (int i = 0; i < frames.Count; i++)
        {
            (int size, byte[] png) = frames[i];
            ArgumentOutOfRangeException.ThrowIfLessThan(size, 1, nameof(frames));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(size, 256, nameof(frames));

            Span<byte> entry = span.Slice(HeaderSize + EntrySize * i, EntrySize);

            // 256 does not fit a byte; the format writes it as 0.
            entry[0] = (byte)(size == 256 ? 0 : size);
            entry[1] = entry[0];
            BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);

            png.CopyTo(span[offset..]);
            offset += png.Length;
        }

        return file;
    }
}
