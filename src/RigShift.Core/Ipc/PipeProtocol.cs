using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace RigShift.Core.Ipc;

/// <summary>A second <c>RigShift.exe</c> hands its command line to the running instance. Empty = show the window.</summary>
public sealed record PipeRequest(IReadOnlyList<string> Arguments);

public sealed record PipeResponse(int ExitCode, string Output);

/// <summary>
/// Framing on <c>\\.\pipe\RigShift</c>: a 4-byte little-endian length, then UTF-8 JSON. One request, one response per
/// connection. Transport-agnostic (any <see cref="Stream"/>) so it is testable without pipes.
/// </summary>
public static class PipeProtocol
{
    public const string PipeName = "RigShift";

    /// <summary>Guards against a garbage length from a foreign client; real messages are a few hundred bytes.</summary>
    private const int MaxMessageBytes = 1024 * 1024;

    public static Task WriteRequestAsync(Stream stream, PipeRequest request, CancellationToken cancellationToken) =>
        WriteAsync(stream, request, PipeJsonContext.Default.PipeRequest, cancellationToken);

    public static Task<PipeRequest> ReadRequestAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, PipeJsonContext.Default.PipeRequest, cancellationToken);

    public static Task WriteResponseAsync(Stream stream, PipeResponse response, CancellationToken cancellationToken) =>
        WriteAsync(stream, response, PipeJsonContext.Default.PipeResponse, cancellationToken);

    public static Task<PipeResponse> ReadResponseAsync(Stream stream, CancellationToken cancellationToken) =>
        ReadAsync(stream, PipeJsonContext.Default.PipeResponse, cancellationToken);

    private static async Task WriteAsync<T>(Stream stream, T message, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message, type);
        byte[] header = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<T> ReadAsync<T>(Stream stream, JsonTypeInfo<T> type, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        byte[] header = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 0 or > MaxMessageBytes)
        {
            throw new InvalidDataException($"Pipe message length {length} is out of range.");
        }

        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return JsonSerializer.Deserialize(payload, type) ?? throw new InvalidDataException("Empty pipe message.");
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PipeRequest))]
[JsonSerializable(typeof(PipeResponse))]
internal sealed partial class PipeJsonContext : JsonSerializerContext;
