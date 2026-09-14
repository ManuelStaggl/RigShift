using RigShift.Core.Ipc;
using Shouldly;
using Xunit;

namespace RigShift.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void PipeName_ContainsCurrentSessionId()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();

        PipeProtocol.PipeName.ShouldBe("RigShift." + current.SessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        PipeProtocol.PipeNameForSession(2).ShouldBe("RigShift.2");
    }

    [Fact]
    public async Task RequestAndResponse_RoundTrip()
    {
        using var stream = new MemoryStream();

        await PipeProtocol.WriteRequestAsync(stream, new PipeRequest(["apply", "Sim Rig ü"]), TestContext.Current.CancellationToken);
        await PipeProtocol.WriteResponseAsync(stream, new PipeResponse(3, "Rig: RolledBack"), TestContext.Current.CancellationToken);
        stream.Position = 0;

        PipeRequest request = await PipeProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken);
        PipeResponse response = await PipeProtocol.ReadResponseAsync(stream, TestContext.Current.CancellationToken);

        request.Arguments.ShouldBe(["apply", "Sim Rig ü"]);
        response.ShouldBe(new PipeResponse(3, "Rig: RolledBack"));
    }

    [Fact]
    public async Task Read_OversizedLength_Throws()
    {
        using var stream = new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F]);

        await Should.ThrowAsync<InvalidDataException>(() => PipeProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Read_TruncatedMessage_Throws()
    {
        using var stream = new MemoryStream([10, 0, 0, 0, (byte)'{']);

        await Should.ThrowAsync<EndOfStreamException>(() => PipeProtocol.ReadRequestAsync(stream, TestContext.Current.CancellationToken));
    }
}
