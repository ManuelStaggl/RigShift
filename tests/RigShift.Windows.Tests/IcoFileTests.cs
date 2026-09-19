using System.Buffers.Binary;
using RigShift.Windows.Shell;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

/// <summary>The <c>.ico</c> layout a game shortcut's composed icon is written in.</summary>
public sealed class IcoFileTests
{
    [Fact]
    public void Build_WritesHeaderEntriesAndFramesInOrder()
    {
        byte[] small = [1, 2, 3];
        byte[] large = [4, 5, 6, 7, 8];

        byte[] file = IcoFile.Build([(16, small), (256, large)]);

        BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(0)).ShouldBe((ushort)0);
        BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)).ShouldBe((ushort)1);
        BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)).ShouldBe((ushort)2);

        // First entry: 16 px, its data right after both entries.
        file[6].ShouldBe((byte)16);
        BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(12)).ShouldBe((ushort)32);
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(14)).ShouldBe(3u);
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(18)).ShouldBe(38u);

        // Second entry: 256 px is written as 0.
        file[22].ShouldBe((byte)0);
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(30)).ShouldBe(5u);
        BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(34)).ShouldBe(41u);

        file[38..].ShouldBe([1, 2, 3, 4, 5, 6, 7, 8]);
    }

    [Fact]
    public void Build_NoFrames_Throws()
    {
        Should.Throw<ArgumentException>(() => IcoFile.Build([]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(257)]
    public void Build_SizeOutsideTheFormat_Throws(int size)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => IcoFile.Build([(size, [1])]));
    }
}
