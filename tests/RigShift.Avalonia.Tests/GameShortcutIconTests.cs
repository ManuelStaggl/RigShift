using System.Buffers.Binary;
using System.Drawing;
using RigShift.Avalonia.Services;
using Serilog.Core;
using Shouldly;
using Xunit;

namespace RigShift.Avalonia.Tests;

/// <summary>The game shortcut's icon: the game in front of a RigShift card, every shell size drawn on its own.</summary>
public sealed class GameShortcutIconTests : IDisposable
{
    /// <summary>Any file with an icon will do; this one is on every Windows.</summary>
    private static readonly string IconSource = Path.Combine(Environment.SystemDirectory, "shell32.dll");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "RigShift.Tests." + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void Create_WritesEveryShellSizeAsPng()
    {
        byte[] file = File.ReadAllBytes(CreateIcon());

        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        count.ShouldBe(10);
        file[6].ShouldBe((byte)16);
        file[6 + 16 * (count - 1)].ShouldBe((byte)0, "256 px is written as 0");
        for (int i = 0; i < count; i++)
        {
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + 16 * i + 12));
            file.AsSpan(offset, 4).ToArray().ShouldBe(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
        }
    }

    /// <summary>The card shows at the top right, the corner at the top left stays free – the stacked look.</summary>
    [Fact]
    public void Create_CardBehindTheGameAtTheTopRight()
    {
        byte[] file = File.ReadAllBytes(CreateIcon());
        int last = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4)) - 1;
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + 16 * last + 8));
        int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(6 + 16 * last + 12));

        using var stream = new MemoryStream(file, offset, length);
        using var image = new Bitmap(stream);
        image.Width.ShouldBe(256);

        Color topRight = image.GetPixel(245, 10);
        topRight.B.ShouldBeGreaterThan(topRight.R, "blue above red: the RigShift card");
        topRight.A.ShouldBe((byte)255);

        image.GetPixel(10, 10).A.ShouldBe((byte)0, "nothing above the game at the top left");
    }

    [Fact]
    public void Delete_RemovesTheIcon()
    {
        string file = CreateIcon();
        GameShortcutIcon.Delete(Guid.Empty, _directory, Logger.None);
        File.Exists(file).ShouldBeFalse();
    }

    [Fact]
    public void Create_FileWithoutIcon_ReturnsNull()
    {
        GameShortcutIcon.Create(Guid.Empty, Path.Combine(_directory, "missing.exe"), _directory, Logger.None).ShouldBeNull();
    }

    private string CreateIcon() =>
        GameShortcutIcon.Create(Guid.Empty, IconSource, _directory, Logger.None).ShouldNotBeNull();
}
