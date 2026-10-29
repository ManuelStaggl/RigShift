using System.Buffers.Binary;
using System.Text;
using RigShift.Core.Fov;
using RigShift.Core.Profiles;
using RigShift.Windows.Display;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace RigShift.Windows.Tests;

public sealed class EdidTests
{
    [Theory]
    [InlineData(@"\\?\DISPLAY#XEC2389#4&1a2b3c4d&0&UID20531#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}", @"SYSTEM\CurrentControlSet\Enum\DISPLAY\XEC2389\4&1a2b3c4d&0&UID20531\Device Parameters")]
    [InlineData(@"\\?\DISPLAY#SAM749B#5&1a2b3c&0&UID4352#{guid}", @"SYSTEM\CurrentControlSet\Enum\DISPLAY\SAM749B\5&1a2b3c&0&UID4352\Device Parameters")]
    public void RegistryKeyFor_TakesPnpIdAndInstanceFromThePath(string path, string expected)
    {
        Edid.RegistryKeyFor(path).ShouldBe(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(@"\\?\DISPLAY#SAM749B")]
    [InlineData(@"\\?\USB#VID_0EB7&PID_0E04#123#{guid}")]
    [InlineData(@"\\?\DISPLAY#..\..#x#{guid}")]
    public void RegistryKeyFor_RefusesPathsThatAreNotAMonitor(string path)
    {
        Edid.RegistryKeyFor(path).ShouldBeNull();
    }

    [Fact]
    public void PictureSize_PrefersTheMillimetresOfTheFirstDetailedTiming()
    {
        byte[] edid = new byte[128];
        edid[21] = 60;
        edid[22] = 34;
        // 598 = 0x256 → low byte 0x56, high nibble 2; 336 = 0x150 → low byte 0x50, high nibble 1.
        edid[66] = 0x56;
        edid[67] = 0x50;
        edid[68] = 0x21;

        Edid.PictureSize(edid).ShouldBe(new ScreenSize(598, 336));
    }

    [Fact]
    public void PictureSize_FallsBackToCentimetres_AndReportsNothingForZero()
    {
        byte[] edid = new byte[128];
        edid[21] = 60;
        edid[22] = 34;
        Edid.PictureSize(edid).ShouldBe(new ScreenSize(600, 340));

        Edid.PictureSize(new byte[128]).ShouldBeNull();
        Edid.PictureSize(new byte[64]).ShouldBeNull();
    }

    [Fact]
    public void SerialHash_TellsTwinsApart_AndStaysTheSame()
    {
        // The two CM27X3 of the reference rig report 0x11 and 0x22 in bytes 12–15 and no serial text (K-03).
        string first = Edid.SerialHash(WithSerial(0x11)).ShouldNotBeNull();

        first.Length.ShouldBe(16);
        Edid.SerialHash(WithSerial(0x22)).ShouldNotBe(first);
        Edid.SerialHash(WithSerial(0x11)).ShouldBe(first);
    }

    [Fact]
    public void SerialHash_ReadsTheSerialText_WhereTheNumberIsAFiller()
    {
        // The XG32UCWG reports 0x01010101 as number and its real serial number as text in a 0xFF descriptor.
        string? one = Edid.SerialHash(WithSerial(0x01010101, "T9LMQS000001"));

        one.ShouldNotBeNull();
        Edid.SerialHash(WithSerial(0x01010101, "T9LMQS000002")).ShouldNotBe(one);
        Edid.SerialHash(WithSerial(0x01010101)).ShouldNotBe(one);
    }

    [Fact]
    public void SerialHash_IsNull_WithoutSerialNumber()
    {
        Edid.SerialHash(new byte[128]).ShouldBeNull();
        Edid.SerialHash(new byte[64]).ShouldBeNull();
    }

    /// <summary>A base block with the header, the given sizes and a correct checksum.</summary>
    private static byte[] Valid(int widthCm = 60, int heightCm = 34, int detailedWidth = 598, int detailedHeight = 336)
    {
        byte[] edid = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(edid, 0);
        edid[21] = (byte)widthCm;
        edid[22] = (byte)heightCm;
        edid[66] = (byte)detailedWidth;
        edid[67] = (byte)detailedHeight;
        edid[68] = (byte)(((detailedWidth >> 8) << 4) | (detailedHeight >> 8));
        edid[127] = (byte)(256 - (edid.Take(127).Sum(b => b) % 256));
        return edid;
    }

    [Fact]
    public void IsValid_WantsTheHeaderAndAChecksumOfZero()
    {
        Edid.IsValid(Valid()).ShouldBeTrue();

        byte[] badSum = Valid();
        badSum[127]++;
        Edid.IsValid(badSum).ShouldBeFalse();

        byte[] badHeader = Valid();
        badHeader[1] = 0;
        Edid.IsValid(badHeader).ShouldBeFalse();

        Edid.IsValid(new byte[64]).ShouldBeFalse();
    }

    [Fact]
    public void PictureSize_TakesTheBasicBlock_WhenTheDetailedTimingIsInCentimetres()
    {
        Edid.PictureSize(Valid(detailedWidth: 60, detailedHeight: 34)).ShouldBe(new ScreenSize(600, 340));
    }

    [Fact]
    public void Reader_IgnoresABrokenEdid_AndSaysSoOnce()
    {
        byte[] broken = Valid();
        broken[127]++;
        var log = new CollectingSink();
        var reader = new EdidDisplaySizeReader(new LoggerConfiguration().WriteTo.Sink(log).CreateLogger(), (_, _) => broken);
        var display = new DisplayIdentity { AdapterDevicePath = "adapter", TargetDevicePath = @"\\?\DISPLAY#XEC2389#4&1a2b3c4d&0&UID20531#{guid}", FriendlyName = "CM27X3" };

        reader.Read(display).ShouldBeNull();
        reader.Read(display).ShouldBeNull();

        log.Events.ShouldHaveSingleItem().Level.ShouldBe(LogEventLevel.Warning);
    }

    [Fact]
    public void Reader_ReturnsTheSizeOfAValidEdid() =>
        new EdidDisplaySizeReader(Logger.None, (_, _) => Valid())
            .Read(new DisplayIdentity { AdapterDevicePath = "adapter", TargetDevicePath = @"\\?\DISPLAY#XEC2389#x#{guid}" })
            .ShouldBe(new ScreenSize(598, 336));

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    /// <summary>128 bytes with the serial number in bytes 12–15 and, optionally, a serial text in the second descriptor.</summary>
    private static byte[] WithSerial(uint number, string? text = null)
    {
        byte[] edid = new byte[128];
        BinaryPrimitives.WriteUInt32LittleEndian(edid.AsSpan(12), number);
        if (text is not null)
        {
            // Descriptor at 72: 00 00 00 FF 00, then 13 bytes of text, ended by a line feed and padded with spaces.
            edid[75] = 0xFF;
            Encoding.ASCII.GetBytes((text + "\n").PadRight(13, ' ')).CopyTo(edid, 77);
        }

        return edid;
    }
}
